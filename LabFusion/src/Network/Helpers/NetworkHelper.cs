using LabFusion.Data;
using LabFusion.Player;
using LabFusion.Senders;

namespace LabFusion.Network;

/// <summary>
/// Helper class for calling basic methods on the Server or Client.
/// </summary>
public static class NetworkHelper
{
    /// <summary>
    /// Starts a server if there is currently none active.
    /// </summary>
    public static void StartServer()
    {
        NetworkLayerManager.Layer?.StartServer();
    }

    /// <summary>
    /// Disconnects the network layer and cleans up.
    /// </summary>
    public static void Disconnect(string reason = "")
    {
        NetworkLayerManager.Layer?.Disconnect(reason);
    }

    /// <summary>
    /// Attempts to join a server given a relay address.
    /// </summary>
    /// <param name="address"></param>
    public static void JoinServerByAddress(string address)
    {
        NetworkLayerManager.Layer?.JoinServerByAddress(address);
    }

    /// <summary>
    /// Gets the address of the currently connected dedicated server.
    /// </summary>
    /// <returns>The relay address.</returns>
    public static string GetServerAddress()
    {
        if (NetworkLayerManager.Layer is DedicatedServerNetworkLayer _layer)
        {
            return _layer.GetServerAddress();
        }

        return string.Empty;
    }

    /// <summary>
    /// Returns true if this user is friended on the active network platform.
    /// </summary>
    /// <param name="userId"></param>
    /// <returns></returns>
    public static bool IsFriend(ulong userId)
    {
        if (NetworkLayerManager.Layer != null)
            return NetworkLayerManager.Layer.IsFriend(userId);

        return false;
    }

    /// <summary>
    /// Kicks a user from the game.
    /// </summary>
    /// <param name="id"></param>
    public static void KickUser(PlayerID id)
    {
        if (NetworkLayerManager.Layer is DedicatedServerNetworkLayer _layer)
        {
            _layer.DisconnectUser(id.PlatformID);
            return;
        }

        ConnectionSender.SendDisconnect(id, "Kicked from Server");
    }

    /// <summary>
    /// Bans a user from the game.
    /// </summary>
    /// <param name="id"></param>
    public static void BanUser(PlayerID id)
    {
        if (NetworkLayerManager.Layer is DedicatedServerNetworkLayer _layer)
        {
            _layer.BanUser(id.SmallID, "Banned from Server");
            return;
        }

        BanManager.Ban(new PlayerInfo(id), "Banned");
        ConnectionSender.SendDisconnect(id, "Banned from Server");
    }

    /// <summary>
    /// Checks if a user is banned.
    /// </summary>
    /// <param name="longID"></param>
    /// <returns></returns>
    public static bool IsBanned(ulong longID)
    {
        if (NetworkLayerManager.Layer is DedicatedServerNetworkLayer)
            return false;

        // Check the ban list
        foreach (var ban in BanManager.BanList.Bans)
        {
            if (ban.Player.PlatformID == longID)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Pardons a user from the ban list.
    /// </summary>
    /// <param name="longId"></param>
    public static void PardonUser(ulong longId)
    {
        if (NetworkLayerManager.Layer is DedicatedServerNetworkLayer _layer)
        {
            if (DedicatedServerNetworkLayer.TryGetRelayPersistentId(longId, out var _persistentPlayerId)) _layer.UnbanUser(_persistentPlayerId);
            return;
        }
        BanManager.Pardon(longId);
    }
}
