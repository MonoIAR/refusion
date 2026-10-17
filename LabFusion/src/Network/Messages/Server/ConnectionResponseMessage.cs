using LabFusion.Data;
using LabFusion.Entities;
using LabFusion.Network.Serialization;
using LabFusion.Player;
using LabFusion.Senders;
using LabFusion.Utilities;
using Refusion.Transport;

namespace LabFusion.Network;

public class ConnectionResponseData : INetSerializable
{
    public PlayerID PlayerID = null;
    public bool IsInitialJoin = false;

    public int? GetSize() => PlayerID.GetSize() + sizeof(bool);

    public void Serialize(INetSerializer serializer)
    {
        serializer.SerializeValue(ref PlayerID);
        serializer.SerializeValue(ref IsInitialJoin);
    }

    public static ConnectionResponseData Create(PlayerID id, bool isInitialJoin)
    {
        return new ConnectionResponseData()
        {
            PlayerID = id,
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

#if DEBUG
        FusionLogger.Log($"Received connection response for {data.PlayerID.PlatformID}, small id {data.PlayerID.SmallID}, mine: {isMine}, existing: {(existing != null ? existing.SmallID.ToString() : "none")}.");
#endif

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

                NetworkPlayerManager.CreateNetworkPlayer(existing);
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

            WorldCatchup.SetStage(JoinStage.FusionRegistered);

            // Our uplink is now open: broadcast our profile and avatar so the host can relay them to everyone
            ConnectionSender.SendClientProfile();
            PlayerSender.SendPlayerAvatar(RigData.RigAvatarStats, RigData.GetAvatarBarcode());
        }
        // Otherwise, create a network player
        else
        {
            InternalServerHelpers.OnPlayerJoined(data.PlayerID, data.IsInitialJoin);

            NetworkPlayerManager.CreateNetworkPlayer(data.PlayerID);
        }
    }
}
