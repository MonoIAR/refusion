using LabFusion.Data;
using LabFusion.Player;
using LabFusion.Representation;
using LabFusion.Utilities;
using LabFusion.Scene;
using LabFusion.Preferences.Server;
using LabFusion.Senders;
using LabFusion.Entities;
using LabFusion.Network.Serialization;
using LabFusion.Safety;

namespace LabFusion.Network;

public class ConnectionRequestData : INetSerializable
{
    public ulong BackupPlatformID;
    public Version Version;
    public string AvatarBarcode;
    public SerializedAvatarStats AvatarStats;
    public Dictionary<string, string> InitialMetadata;
    public List<string> InitialEquippedItems;

    public int? GetSize() => sizeof(ulong) + Version.GetSize() + AvatarBarcode.GetSize() + SerializedAvatarStats.Size + InitialMetadata.GetSize() + InitialEquippedItems.GetSize();

    public bool IsValid { get; private set; } = true;

    public void Serialize(INetSerializer serializer)
    {
        try
        {
            serializer.SerializeValue(ref BackupPlatformID);
            serializer.SerializeValue(ref Version);
            serializer.SerializeValue(ref AvatarBarcode);
            serializer.SerializeValue(ref AvatarStats);
            serializer.SerializeValue(ref InitialMetadata);
            serializer.SerializeValue(ref InitialEquippedItems);
        }
        catch (Exception e)
        {
            IsValid = false;

            FusionLogger.LogException("serializing ConnectionRequestData", e);
        }
    }

    public static ConnectionRequestData Create(ulong longId, Version version, string avatarBarcode, SerializedAvatarStats stats)
    {
        LocalPlayer.InvokeApplyInitialMetadata();

        return new ConnectionRequestData()
        {
            BackupPlatformID = longId,
            Version = version,
            AvatarBarcode = avatarBarcode,
            AvatarStats = stats,
            InitialMetadata = LocalPlayer.Metadata.Metadata.LocalDictionary,
            InitialEquippedItems = InternalServerHelpers.GetInitialEquippedItems(),
        };
    }
}

public class ConnectionRequestMessage : NativeMessageHandler
{
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

        // Make sure we aren't loading
        if (FusionSceneManager.IsLoading())
        {
            Deny(platformID, "The server is loading a level. Please try again later.");
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
                ReacceptConnection(existing, platformID, data);
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

        // Append metadata with info
        data.InitialMetadata[nameof(PlayerMetadata.PermissionLevel)] = level.ToString();

        // Create new PlayerID
        var playerId = new PlayerID(platformID, newSmallId.Value, data.InitialMetadata, data.InitialEquippedItems);

        // Finally, check for dynamic connection disallowing
        if (!MultiplayerHooking.CheckShouldAllowConnection(playerId, out string reason))
        {
            Deny(platformID, reason);
            return;
        }

        // All checks have succeeded, let the player into the server
        OnConnectionAllowed(playerId, platformID, data);
    }

    private static void Deny(ulong platformID, string reason)
    {
        FusionLogger.Warn($"Denied connection request from {platformID}: {reason}");
        ConnectionSender.SendConnectionDeny(platformID, reason);
    }

    private static void OnConnectionAllowed(PlayerID playerID, ulong platformID, ConnectionRequestData data)
    {
        // Reserve the player's smallID so that other players don't steal it
        PlayerIDManager.ReserveSmallID(playerID.SmallID);

        FusionLogger.Log($"Accepted connection from {platformID} as small id {playerID.SmallID}.");

        SendJoinAndCatchup(playerID, platformID, data);
    }

    private static void ReacceptConnection(PlayerID playerID, ulong platformID, ConnectionRequestData data)
    {
        SendJoinAndCatchup(playerID, platformID, data);
    }

    private static void SendJoinAndCatchup(PlayerID playerID, ulong platformID, ConnectionRequestData data)
    {
        // Send the new player to all existing players (and the new player so they know they exist)
        ConnectionSender.SendPlayerJoin(playerID, data.AvatarBarcode, data.AvatarStats);

        // Now we send all of our other players to the new player
        foreach (var id in PlayerIDManager.PlayerIDs)
        {
            // Don't resend the new player to themselves
            if (id.SmallID == playerID.SmallID)
            {
                continue;
            }

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

            ConnectionSender.SendPlayerCatchup(platformID, id, barcode, stats);
        }

        // Now, make sure the player loads into the scene
        LoadSender.SendLevelLoad(FusionSceneManager.Barcode, FusionSceneManager.LoadBarcode, platformID);

        // Send the dynamics list
        var assignData = DynamicsAssignData.Create();

        using (var writer = NetWriter.Create(assignData.GetSize()))
        {
            assignData.Serialize(writer);

            using var message = NetMessage.Create(NativeMessageTag.DynamicsAssignment, writer, CommonMessageRoutes.None);
            MessageSender.SendFromServer(platformID, NetworkChannel.Reliable, message);
        }

        // Send the active server settings
        LobbyInfoManager.SendLobbyInfo(platformID);
    }
}
