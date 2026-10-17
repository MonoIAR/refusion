using LabFusion.Data;
using LabFusion.Entities;
using LabFusion.Network.Serialization;
using LabFusion.Player;
using LabFusion.Preferences.Server;
using LabFusion.Representation;
using LabFusion.Scene;
using LabFusion.Senders;
using LabFusion.Utilities;

namespace LabFusion.Network;

public class ConnectionRequestData : INetSerializable
{
    public ulong BackupPlatformID;
    public Version Version;
    public string Username;

    public int? GetSize() => sizeof(ulong) + Version.GetSize() + Username.GetSize();

    public bool IsValid { get; private set; } = true;

    public void Serialize(INetSerializer serializer)
    {
        try
        {
            serializer.SerializeValue(ref BackupPlatformID);
            serializer.SerializeValue(ref Version);
            serializer.SerializeValue(ref Username);
        }
        catch (Exception e)
        {
            IsValid = false;

            FusionLogger.LogException("serializing ConnectionRequestData", e);
        }
    }

    public static ConnectionRequestData Create(ulong longId, Version version)
    {
        LocalPlayer.InvokeApplyInitialMetadata();

        return new ConnectionRequestData()
        {
            BackupPlatformID = longId,
            Version = version,
            Username = LocalPlayer.Username,
        };
    }
}

public class ConnectionRequestMessage : NativeMessageHandler
{
    private static DateTime _lastLoadingDropWarnUtc = DateTime.MinValue;

    public override byte Tag => NativeMessageTag.ConnectionRequest;

    public override ExpectedReceiverType ExpectedReceiver => ExpectedReceiverType.ServerOnly;

    protected override void OnHandleMessage(ReceivedMessage received)
    {
        var data = received.ReadData<ConnectionRequestData>();

        ulong platformID = received.PlatformID ?? data.BackupPlatformID;

        // If the connection request is invalid, deny it
        if (!data.IsValid)
        {
            Deny(platformID, "Connection request was invalid. You are likely on mismatching versions.");
            return;
        }

        // Make sure we aren't loading. The request is dropped instead of denied, so retrying clients connect as soon as loading finishes
        if (FusionSceneManager.IsLoading())
        {
            if (DateTime.UtcNow - _lastLoadingDropWarnUtc > TimeSpan.FromSeconds(5))
            {
                _lastLoadingDropWarnUtc = DateTime.UtcNow;
                FusionLogger.Log($"Dropped a connection request from {platformID} while the scene is loading.");
            }
            return;
        }

        byte? newSmallId = null;
        if (NetworkLayerManager.Layer is DedicatedServerNetworkLayer && received.PlatformID.HasValue && DedicatedServerNetworkLayer.TryGetRelaySmallId(received.PlatformID.Value, out var _relaySmallId))
        {
            newSmallId = _relaySmallId;
        }
        else if (NetworkLayerManager.Layer is not DedicatedServerNetworkLayer)
        {
            newSmallId = PlayerIDManager.GetUniquePlayerID();
        }

        // Clients retry connection requests, so an already accepted player asking again gets a re-send instead of a deny
        var existing = PlayerIDManager.GetPlayerID(platformID);

        if (existing != null)
        {
            if (newSmallId.HasValue && existing.SmallID == newSmallId.Value)
            {
                FusionLogger.Warn($"Re-accepting connection request from {platformID} (small id {existing.SmallID}).");
                ReacceptConnection(existing, platformID);
                return;
            }

            FusionLogger.Warn($"Connection request from {platformID} had a stale small id; replacing the old entry.");
            existing.Cleanup();
        }

        // No unused ids available
        if (!newSmallId.HasValue || PlayerIDManager.HasPlayerID(newSmallId.Value))
        {
            Deny(platformID, "Server ran out of space! Wait for someone to leave.");
            return;
        }

        // Check if theres too many players
        if (PlayerIDManager.PlayerCount >= byte.MaxValue || PlayerIDManager.PlayerCount >= SavedServerSettings.MaxPlayers.Value)
        {
            Deny(platformID, "Server is full! Wait for someone to leave.");
            return;
        }

        // Verify joining
        bool isVerified = NetworkVerification.IsClientApproved(platformID);

        if (!isVerified)
        {
            Deny(platformID, "Server is private.");
            return;
        }

        // Compare versions
        VersionResult versionResult = NetworkVerification.CompareVersion(FusionMod.Version, data.Version);

        if (versionResult != VersionResult.Ok)
        {
            switch (versionResult)
            {
                default:
                case VersionResult.Unknown:
                    Deny(platformID, "Unknown Version Mismatch");
                    break;
                case VersionResult.Lower:
                    Deny(platformID, "Server is on an older version. Downgrade your version or notify the host.");
                    break;
                case VersionResult.Higher:
                    Deny(platformID, "Server is on a newer version. Update your version.");
                    break;
            }

            return;
        }

        // Get the permission level
        FusionPermissions.FetchPermissionLevel(platformID, out var level, out _);

        // Check for banning
        if (NetworkHelper.IsBanned(platformID))
        {
            Deny(platformID, "Banned from Server");
            return;
        }

        // Build the initial metadata for this player
        string username = string.IsNullOrWhiteSpace(data.Username) ? "Player" : data.Username;

        var metadata = new Dictionary<string, string>()
        {
            [nameof(PlayerMetadata.Username)] = username,
            [nameof(PlayerMetadata.PermissionLevel)] = level.ToString(),
        };

        // Create new PlayerID
        var playerId = new PlayerID(platformID, newSmallId.Value, metadata, new List<string>());

        // Finally, check for dynamic connection disallowing
        if (!MultiplayerHooking.CheckShouldAllowConnection(playerId, out string reason))
        {
            Deny(platformID, reason);
            return;
        }

        // All checks have succeeded, let the player into the server
        OnConnectionAllowed(playerId, platformID);
    }

    private static void Deny(ulong platformID, string reason)
    {
        FusionLogger.Warn($"Denied connection request from {platformID}: {reason}");
        ConnectionSender.SendConnectionDeny(platformID, reason);
    }

    private static void OnConnectionAllowed(PlayerID playerID, ulong platformID)
    {
        // Reserve the player's smallID so that other players don't steal it
        PlayerIDManager.ReserveSmallID(playerID.SmallID);

        FusionLogger.Log($"Accepted connection from {platformID} as small id {playerID.SmallID}.");

        SendJoinAndCatchup(playerID, platformID);
    }

    private static void ReacceptConnection(PlayerID playerID, ulong platformID)
    {
        SendJoinAndCatchup(playerID, platformID);
    }

    private static void SendJoinAndCatchup(PlayerID playerID, ulong platformID)
    {
#if DEBUG
        DedicatedServerNetworkLayer.LastJoinSendUtc = DateTime.UtcNow;
#endif

        // Send the new player to all existing players (and the new player so they know they exist)
        ConnectionSender.SendPlayerJoin(playerID);

        var others = new List<PlayerID>();

        foreach (var id in PlayerIDManager.PlayerIDs)
        {
            if (id.SmallID == playerID.SmallID)
            {
                continue;
            }

            others.Add(id);
        }

        // Send the other players to the new player
        foreach (var id in others)
        {
            ConnectionSender.SendPlayerCatchup(platformID, id);
        }

        // Send the other players' avatars to the new player
        foreach (var id in others)
        {
            string barcode;
            SerializedAvatarStats stats;

            if (id.SmallID == PlayerIDManager.HostSmallID)
            {
                barcode = RigData.RigAvatarId;
                stats = RigData.RigAvatarStats;
            }
            else if (NetworkPlayerManager.TryGetPlayer(id.SmallID, out var rep))
            {
                barcode = rep.AvatarSetter.AvatarBarcode;
                stats = rep.AvatarSetter.AvatarStats;
            }
            else
            {
                continue;
            }

            ConnectionSender.SendPlayerAvatarTo(playerID.SmallID, platformID, id, barcode, stats);
        }

        // Send the other players' full profiles to the new player
        foreach (var id in others)
        {
            ConnectionSender.SendPlayerProfileTo(playerID.SmallID, platformID, id);
        }

        // Now, make sure the player loads into the scene
        LoadSender.SendLevelLoad(FusionSceneManager.Barcode, FusionSceneManager.LoadBarcode, platformID);

        // Send the dynamics list, chunked to stay under the datagram limit
        SendDynamicsAssignment(platformID);

        // Send the active server settings
        LobbyInfoManager.SendLobbyInfo(platformID);
    }

    private static byte _dynamicsTransferId = 0;

    private const int DynamicsChunkSize = 640;

    private static void SendDynamicsAssignment(ulong platformID)
    {
        var assignData = DynamicsAssignData.Create();

        byte[] payload;
        using (var writer = NetWriter.Create(assignData.GetSize()))
        {
            assignData.Serialize(writer);
            payload = writer.Buffer.ToArray();
        }

        int chunkCount = System.Math.Max(1, (payload.Length + DynamicsChunkSize - 1) / DynamicsChunkSize);

        _dynamicsTransferId++;

        for (ushort i = 0; i < chunkCount; i++)
        {
            int offset = i * DynamicsChunkSize;
            int length = System.Math.Min(DynamicsChunkSize, payload.Length - offset);

            var chunkData = new ChunkedPayloadData()
            {
                InnerTag = NativeMessageTag.DynamicsAssignment,
                TransferId = _dynamicsTransferId,
                ChunkIndex = i,
                ChunkCount = (ushort)chunkCount,
                TotalLength = payload.Length,
                Payload = payload[offset..(offset + length)],
            };

            using var chunkWriter = NetWriter.Create(chunkData.GetSize());
            chunkData.Serialize(chunkWriter);

            using var message = NetMessage.Create(NativeMessageTag.ChunkedPayload, chunkWriter, CommonMessageRoutes.None);
            MessageSender.SendFromServer(platformID, NetworkChannel.Reliable, message);

#if DEBUG
            FusionLogger.Log($"Sent dynamics chunk {i + 1}/{chunkCount} to {platformID} ({message.Length} bytes).");
#endif
        }
    }
}
