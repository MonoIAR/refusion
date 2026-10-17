using LabFusion.Data;
using LabFusion.Network;
using LabFusion.Network.Serialization;
using LabFusion.Player;
using LabFusion.Representation;
using LabFusion.Utilities;

namespace LabFusion.Senders;

public static class ConnectionSender
{
    public static void SendDisconnectToAll(string reason = "")
    {
        if (NetworkInfo.IsHost)
        {
            foreach (var id in PlayerIDManager.PlayerIDs)
            {
                if (id.IsMe)
                    continue;

                using var writer = NetWriter.Create();
                var disconnect = DisconnectMessageData.Create(id.PlatformID, reason);
                writer.SerializeValue(ref disconnect);

                using var message = NetMessage.Create(NativeMessageTag.Disconnect, writer, CommonMessageRoutes.None);
                MessageSender.SendFromServer(id.PlatformID, NetworkChannel.Reliable, message);

                NetworkConnectionManager.TimeoutDisconnect(id.PlatformID);
            }
        }
    }

    public static void SendDisconnect(ulong platformID, string reason = "")
    {
        if (NetworkInfo.IsHost)
        {
            using var writer = NetWriter.Create();
            var disconnect = DisconnectMessageData.Create(platformID, reason);
            writer.SerializeValue(ref disconnect);

            using var message = NetMessage.Create(NativeMessageTag.Disconnect, writer, CommonMessageRoutes.None);
            MessageSender.BroadcastMessage(NetworkChannel.Reliable, message);

            NetworkConnectionManager.TimeoutDisconnect(platformID);
        }
    }

    public static void SendConnectionDeny(ulong platformID, string reason = "")
    {
        if (NetworkInfo.IsHost)
        {
            using var writer = NetWriter.Create();
            var disconnect = DisconnectMessageData.Create(platformID, reason);
            writer.SerializeValue(ref disconnect);

            using var message = NetMessage.Create(NativeMessageTag.Disconnect, writer, CommonMessageRoutes.None);
            MessageSender.SendFromServer(platformID, NetworkChannel.Reliable, message);

            NetworkConnectionManager.TimeoutDisconnect(platformID);
        }
    }

    public static void SendConnectionRequest()
    {
        if (NetworkInfo.HasServer)
        {
            using var writer = NetWriter.Create();

            var data = ConnectionRequestData.Create(PlayerIDManager.LocalPlatformID, FusionMod.Version);
            data.Serialize(writer);

            using NetMessage message = NetMessage.Create(NativeMessageTag.ConnectionRequest, writer, CommonMessageRoutes.None);
#if DEBUG
            FusionLogger.Log($"Sent connection request ({message.Length} bytes).");
#endif
            MessageSender.BroadcastMessage(NetworkChannel.Reliable, message);
        }
        else
        {
            FusionLogger.Error("Attempted to send a connection request, but we are not connected to anyone!");
        }
    }

    private static PlayerID CreateTrimmedSnapshot(PlayerID id)
    {
        var metadata = new Dictionary<string, string>();

        if (!id.Metadata.Metadata.LocalDictionary.TryGetValue(nameof(PlayerMetadata.Username), out var username) || string.IsNullOrWhiteSpace(username))
        {
            username = "Player";
        }

        if (!id.Metadata.Metadata.LocalDictionary.TryGetValue(nameof(PlayerMetadata.PermissionLevel), out var permission))
        {
            permission = PermissionLevel.DEFAULT.ToString();
        }

        metadata[nameof(PlayerMetadata.Username)] = username;
        metadata[nameof(PlayerMetadata.PermissionLevel)] = permission;

        return new PlayerID(id.PlatformID, id.SmallID, metadata, new List<string>());
    }

    public static void SendPlayerCatchup(ulong newUser, PlayerID id)
    {
        using var writer = NetWriter.Create();
        var response = ConnectionResponseData.Create(CreateTrimmedSnapshot(id), false);
        writer.SerializeValue(ref response);

        using var message = NetMessage.Create(NativeMessageTag.ConnectionResponse, writer, CommonMessageRoutes.None);
#if DEBUG
        FusionLogger.Log($"Sent catchup response for {id.SmallID} to {newUser} ({message.Length} bytes).");
#endif
        MessageSender.SendFromServer(newUser, NetworkChannel.Reliable, message);
    }

    public static void SendPlayerJoin(PlayerID id)
    {
        using var writer = NetWriter.Create();
        var response = ConnectionResponseData.Create(CreateTrimmedSnapshot(id), true);
        writer.SerializeValue(ref response);

        using var message = NetMessage.Create(NativeMessageTag.ConnectionResponse, writer, CommonMessageRoutes.None);
#if DEBUG
        FusionLogger.Log($"Sent join response for {id.SmallID} ({message.Length} bytes).");
#endif
        MessageSender.BroadcastMessage(NetworkChannel.Reliable, message);
    }

    public static void SendClientProfile()
    {
        var data = ConnectionProfileData.Create();

#if DEBUG
        FusionLogger.Log($"Sent client profile ({data.GetSize()} bytes raw).");
#endif
        MessageRelay.RelayNative(data, NativeMessageTag.ConnectionProfile, CommonMessageRoutes.ReliableToOtherClients);
    }

    public static void SendPlayerProfileTo(byte joinerSmallId, ulong newUser, PlayerID id)
    {
        using var writer = NetWriter.Create();
        var data = ConnectionProfileData.CreateFor(id);
        data.Serialize(writer);

        using var message = NetMessage.Create(NativeMessageTag.ConnectionProfile, writer, new MessageRoute(joinerSmallId, NetworkChannel.Reliable), id.SmallID);
#if DEBUG
        FusionLogger.Log($"Sent profile of {id.SmallID} to {newUser} ({message.Length} bytes).");
#endif
        MessageSender.SendFromServer(newUser, NetworkChannel.Reliable, message);
    }

    public static void SendPlayerAvatarTo(byte joinerSmallId, ulong newUser, PlayerID id, string barcode, SerializedAvatarStats stats)
    {
        using var writer = NetWriter.Create();
        var data = new PlayerRepAvatarData()
        {
            Stats = stats,
            Barcode = barcode,
        };
        data.Serialize(writer);

        using var message = NetMessage.Create(NativeMessageTag.PlayerRepAvatar, writer, new MessageRoute(joinerSmallId, NetworkChannel.Reliable), id.SmallID);
#if DEBUG
        FusionLogger.Log($"Sent avatar of {id.SmallID} to {newUser} ({message.Length} bytes).");
#endif
        MessageSender.SendFromServer(newUser, NetworkChannel.Reliable, message);
    }
}
