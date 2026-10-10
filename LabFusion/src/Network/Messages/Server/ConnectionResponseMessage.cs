using LabFusion.Data;
using LabFusion.Entities;
using LabFusion.Network.Serialization;
using LabFusion.Player;

namespace LabFusion.Network;

public class ConnectionResponseData : INetSerializable
{
    public PlayerID PlayerID = null;
    public string AvatarBarcode = null;
    public SerializedAvatarStats AvatarStats = null;
    public bool IsInitialJoin = false;

    public int? GetSize() => PlayerID.GetSize() + AvatarBarcode.GetSize() + SerializedAvatarStats.Size + sizeof(bool);

    public void Serialize(INetSerializer serializer)
    {
        serializer.SerializeValue(ref PlayerID);
        serializer.SerializeValue(ref AvatarBarcode);
        serializer.SerializeValue(ref AvatarStats);
        serializer.SerializeValue(ref IsInitialJoin);
    }

    public static ConnectionResponseData Create(PlayerID id, string avatarBarcode, SerializedAvatarStats stats, bool isInitialJoin)
    {
        return new ConnectionResponseData()
        {
            PlayerID = id,
            AvatarBarcode = avatarBarcode,
            AvatarStats = stats,
            IsInitialJoin = isInitialJoin,
        };
    }
}

public class ConnectionResponseMessage : NativeMessageHandler
{
    public override byte Tag => NativeMessageTag.ConnectionResponse;

    public override ExpectedReceiverType ExpectedReceiver => ExpectedReceiverType.ClientsOnly;

    protected override void OnHandleMessage(ReceivedMessage received)
    {
        var data = received.ReadData<ConnectionResponseData>();

        bool isMine = data.PlayerID.PlatformID == PlayerIDManager.LocalPlatformID;

        var existing = PlayerIDManager.GetPlayerID(data.PlayerID.PlatformID);

        // Duplicated responses (re-sent joins, host broadcast loopback) must not rebuild existing players
        if (existing != null && existing.SmallID == data.PlayerID.SmallID)
        {
            // Our own half-completed registration is repaired by NetworkPlayerManager instead
            if (isMine)
            {
                return;
            }

            // The id was received previously, but the player was never fully created
            if (!NetworkPlayerManager.TryGetPlayer(existing.SmallID, out _))
            {
                // Clear any residual entity from a half-completed registration before recreating
                var residual = NetworkEntityManager.IDManager.RegisteredEntities.GetEntity(existing.SmallID);

                if (residual != null)
                {
                    NetworkEntityManager.IDManager.UnregisterEntity(residual);
                }

                InternalServerHelpers.OnPlayerJoined(existing, data.IsInitialJoin);

                var missingPlayer = NetworkPlayerManager.CreateNetworkPlayer(existing);
                missingPlayer.AvatarSetter.SwapAvatar(data.AvatarStats, data.AvatarBarcode);

                if (NetworkInfo.IsHost)
                {
                    CatchupPlayer(existing);
                }
            }

            return;
        }

        // The player rejoined with a new small id before the old entry was cleaned up
        if (existing != null)
        {
            existing.Cleanup();
        }

        // Insert the id into our list
        data.PlayerID.Insert();

        // Check the id to see if its our own
        // If it is, just update our self reference
        if (isMine)
        {
            PlayerIDManager.ApplyLocalID();

            NetworkPlayerManager.CreateLocalPlayer();

            InternalServerHelpers.OnJoinServer();
        }
        // Otherwise, create a network player
        else
        {
            InternalServerHelpers.OnPlayerJoined(data.PlayerID, data.IsInitialJoin);

            var networkPlayer = NetworkPlayerManager.CreateNetworkPlayer(data.PlayerID);
            networkPlayer.AvatarSetter.SwapAvatar(data.AvatarStats, data.AvatarBarcode);
        }

        // Send catchup messages now that the user is registered
        if (NetworkInfo.IsHost)
        {
            CatchupPlayer(data.PlayerID);
        }
    }

    private static void CatchupPlayer(PlayerID player)
    {
        // SERVER CATCHUP
        // Catchup hooked events
        CatchupManager.InvokePlayerServerCatchup(player);
    }
}