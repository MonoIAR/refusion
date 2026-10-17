using LabFusion.Network.Serialization;
using LabFusion.Player;
using LabFusion.Utilities;

namespace LabFusion.Network;

public class ConnectionProfileData : INetSerializable
{
    public Dictionary<string, string> Metadata;
    public List<string> EquippedItems;

    public int? GetSize() => Metadata.GetSize() + EquippedItems.GetSize();

    public void Serialize(INetSerializer serializer)
    {
        serializer.SerializeValue(ref Metadata);
        serializer.SerializeValue(ref EquippedItems);
    }

    public static ConnectionProfileData Create()
    {
        return new ConnectionProfileData()
        {
            Metadata = LocalPlayer.Metadata.Metadata.LocalDictionary,
            EquippedItems = InternalServerHelpers.GetInitialEquippedItems(),
        };
    }

    public static ConnectionProfileData CreateFor(PlayerID id)
    {
        return new ConnectionProfileData()
        {
            Metadata = id.Metadata.Metadata.LocalDictionary,
            EquippedItems = id.EquippedItems,
        };
    }
}

public class ConnectionProfileMessage : NativeMessageHandler
{
    public override byte Tag => NativeMessageTag.ConnectionProfile;

    // Both: the host receives the client's profile first (isServerHandled) and relays it via ToOtherClients,
    // then applies its own loopback copy; ClientsOnly would throw before the relay.
    protected override void OnHandleMessage(ReceivedMessage received)
    {
        var data = received.ReadData<ConnectionProfileData>();

        // A profile may only modify the PlayerID that owns it, matched by the transport-verified sender
        if (!received.Sender.HasValue)
        {
            return;
        }

        var playerId = PlayerIDManager.GetPlayerID(received.Sender.Value);

        if (playerId == null)
        {
            return;
        }

        if (data.Metadata != null)
        {
            foreach (var pair in data.Metadata)
            {
                // Permission level is host-authoritative and only arrives via the connection response snapshot
                if (pair.Key == nameof(PlayerMetadata.PermissionLevel))
                {
                    continue;
                }

                playerId.Metadata.Metadata.ForceSetLocalMetadata(pair.Key, pair.Value);
            }
        }

        if (data.EquippedItems != null)
        {
            foreach (var item in data.EquippedItems)
            {
                playerId.ForceSetEquipped(item, true);
            }
        }
    }
}
