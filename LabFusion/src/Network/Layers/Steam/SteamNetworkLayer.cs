using LabFusion.Data;
using LabFusion.Player;
using LabFusion.Utilities;
using LabFusion.UI.Popups;

using Steamworks;
using Steamworks.Data;

using LabFusion.Senders;
using LabFusion.Voice;
using LabFusion.Voice.Unity;
using LabFusion.Scene;
using LabFusion.SDK.Gamemodes;
using Il2CppSLZ.Marrow.Warehouse;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace LabFusion.Network;

public abstract class DedicatedServerNetworkLayer : NetworkLayer
{
    public abstract uint ApplicationID { get; }

    public const int ReceiveBufferSize = 32;

    public override string Title => "Dedicated Server";

    public override string Platform => "DedicatedServer";

    public override bool IsHost => _isServerActive;
    public override bool IsClient => _isConnectionActive;

    private INetworkLobby _currentLobby;
    public override INetworkLobby Lobby => _currentLobby;

    private IVoiceManager _voiceManager = null;
    public override IVoiceManager VoiceManager => _voiceManager;

    private IMatchmaker _matchmaker = null;
    public override IMatchmaker Matchmaker => _matchmaker;

    public SteamId SteamId;

    public static SteamSocketManager SteamSocket;
    public static SteamConnectionManager SteamConnection;

    protected bool _isServerActive = false;
    protected bool _isConnectionActive = false;
    public bool RelayOperator => _isRelayOperator;
    private UdpClient _udpClient;
    private IPEndPoint _relayEndpoint;
    private CancellationTokenSource _receiveSource;
    private int _clientId;
    private byte _smallId;
    private int _ownerClientId;
    private bool _isRelayOperator;
    private string _sessionToken = string.Empty;
    private DateTime _lastPingUtc;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentQueue<RelayPacket> _receivedPackets = new();
    private readonly ConcurrentQueue<RelayPacket> _serverCommands = new();
    private static readonly ConcurrentDictionary<ulong, byte> _relaySmallIds = new();
    private static readonly ConcurrentDictionary<ulong, bool> _relayOperators = new();
    private static readonly ConcurrentDictionary<ulong, string> _relayPersistentIds = new();

    private const string _serverVersion = "0.1.0";
    private const int _protocolVersion = 2;

    // A local reference to a lobby
    // This isn't actually used for joining servers, just for matchmaking
    protected Lobby _localLobby;

    public override bool CheckSupported()
    {
        return !PlatformHelper.IsAndroid;
    }

    public override bool CheckValidation()
    {
        return true;
    }

    public override void OnInitializeLayer()
    {
        RefusionIdentity.OnInitialize();
        PlayerIDManager.SetLongID(GetPlatformId());
        LocalPlayer.Username = Environment.UserName;
        HookSteamEvents();

        // Create managers
        _voiceManager = new UnityVoiceManager();
        _voiceManager.Enable();

        _matchmaker = new DedicatedServerMatchmaker();
    }

    public override void OnDeinitializeLayer()
    {
        _voiceManager.Disable();
        _voiceManager = null;

        _matchmaker = null;

        _localLobby = default;
        _currentLobby = null;

        Disconnect();

        UnHookSteamEvents();
    }

    public override void LogIn()
    {
        InvokeLoggedInEvent();
    }

    public override void LogOut()
    {
        Disconnect();
        InvokeLoggedOutEvent();
    }

    private const string STEAMWORKS_ASSEMBLY_NAME = "Il2CppFacepunch.Steamworks.Win64";

    private static bool GameHasSteamworks()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        foreach (var assembly in assemblies)
        {
            if (assembly.FullName.StartsWith(STEAMWORKS_ASSEMBLY_NAME))
            {
                return true;
            }
        }

        return false;
    }

    private static void ShutdownGameClient()
    {
        FusionLogger.Log("Shutting down the game's Steamworks instance...");

        Il2CppSteamworks.SteamClient.Shutdown();
    }

    public override void OnUpdateLayer()
    {
        if (_udpClient == null || !_isConnectionActive) return;
        while (_receivedPackets.TryDequeue(out var _packet)) HandleRelayPacket(_packet);
        while (_serverCommands.TryDequeue(out var _command)) ExecuteServerCommand(_command);
        if (DateTime.UtcNow - _lastPingUtc > TimeSpan.FromSeconds(5))
        {
            _lastPingUtc = DateTime.UtcNow;
            SendRelayPacket(new RelayPacket { Type = "Ping", ClientId = _clientId });
        }
    }

    public override string GetUsername(ulong userId)
    {
        return PlayerIDManager.GetPlayerID(userId)?.Metadata?.Metadata?.GetMetadata(nameof(PlayerMetadata.Username)) ?? $"Player {userId}";
    }

    public override void SetServerScene(string levelBarcode, string loadingScreenBarcode)
    {
        SendRelayPacket(new RelayPacket { Type = "RawForward", ClientId = _clientId, Mode = "ServerSceneState", LevelBarcode = levelBarcode, LoadingScreenBarcode = loadingScreenBarcode });
    }

    public override void SetServerLoading(bool isLoading)
    {
        SendRelayPacket(new RelayPacket { Type = "RawForward", ClientId = _clientId, Mode = "ServerLoadingState", IsLoading = isLoading });
    }

    public override void SetServerSettings(string settingsJson)
    {
        SendRelayPacket(new RelayPacket { Type = "RawForward", ClientId = _clientId, Mode = "ServerSettingsState", SettingsJson = settingsJson });
    }

    public override bool IsFriend(ulong userId)
    {
        return userId == PlayerIDManager.LocalPlatformID;
    }

    public override void BroadcastMessage(NetworkChannel channel, NetMessage message)
    {
        if (IsHost)
        {
            SendRelayPacket(CreateForwardPacket("ServerBroadcast", channel, message));
        }
        else
        {
            SendRelayPacket(CreateForwardPacket("ClientMessage", channel, message));
        }
    }

    public override void SendToServer(NetworkChannel channel, NetMessage message)
    {
        SendRelayPacket(CreateForwardPacket("ClientMessage", channel, message));
    }

    public override void SendFromServer(byte userId, NetworkChannel channel, NetMessage message)
    {
        var id = PlayerIDManager.GetPlayerID(userId);

        if (id != null)
        {
            SendFromServer(id.PlatformID, channel, message);
        }
    }

    public override void SendFromServer(ulong userId, NetworkChannel channel, NetMessage message)
    {
        // Make sure this is actually the server
        if (!IsHost)
        {
            return;
        }

        // Get the connection from the userid dictionary
        var _target = PlayerIDManager.GetPlayerID(userId);
        if (_target != null) SendRelayPacket(CreateForwardPacket("ServerTarget", channel, message, _target.SmallID));
    }

    public override void StartServer()
    {
        Notifier.Send(new Notification { Title = "Unable to Create Server", Message = "Manual server creation is disabled. Enter a relay address to join a dedicated server.", PopupLength = 5f, ShowPopup = true, Type = NotificationType.ERROR });
    }

    public void JoinServer(SteamId serverId)
    {
        // Leave existing server
        if (_isConnectionActive || _isServerActive)
            Disconnect();

        JoinDedicatedServer(serverId.Value.ToString());
    }

    public override void Disconnect(string reason = "")
    {
        // Make sure we are currently in a server
        if (!_isServerActive && !_isConnectionActive)
            return;

        try
        {
            if (_clientId != 0) SendRelayPacket(new RelayPacket { Type = "Leave", ClientId = _clientId });
            _receiveSource?.Cancel();
            _udpClient?.Dispose();
        }
        catch
        {
            FusionLogger.Log("Error closing socket server / connection manager");
        }

        _isServerActive = false;
        _isConnectionActive = false;
        _udpClient = null;
        _receiveSource = null;
        _clientId = 0;
        _smallId = 0;
        _sessionToken = string.Empty;
        _relaySmallIds.Clear();
        _relayOperators.Clear();
        _relayPersistentIds.Clear();

        InternalServerHelpers.OnDisconnect(reason);
    }


    public override void DisconnectUser(ulong platformID)
    {
        if (!_isServerActive) return;
        var _target = PlayerIDManager.GetPlayerID(platformID);
        if (_target == null) return;
        SendRelayPacket(new RelayPacket { Type = "Kick", ClientId = _clientId, TargetSmallId = _target.SmallID, Reason = "Kicked from server." });
    }

    public void BanUser(byte smallId, string reason)
    {
        SendRelayPacket(new RelayPacket { Type = "Ban", ClientId = _clientId, TargetSmallId = smallId, Reason = reason });
    }

    public void UnbanUser(string persistentPlayerId)
    {
        SendRelayPacket(new RelayPacket { Type = "Unban", ClientId = _clientId, PersistentPlayerId = persistentPlayerId });
    }

    public string ServerCode { get; private set; } = null;

    public override string GetServerCode()
    {
        return ServerCode;
    }

    public override void RefreshServerCode()
    {
        ServerCode = RandomCodeGenerator.GetString(8);

        LobbyInfoManager.PushLobbyUpdate();
    }

    public override void JoinServerByCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        DedicatedServerHistory.Add(code);
        JoinDedicatedServer(code);
    }

    protected virtual void JoinDedicatedServer(string address) => JoinDedicatedServerInternal(address);

    protected void JoinDedicatedServerInternal(string address)
    {
        if (!TryParseAddress(address, out var _endpoint)) { FusionLogger.Error($"Invalid dedicated server address: {address}"); return; }
        if (_isConnectionActive || _isServerActive) Disconnect();
        _relayEndpoint = _endpoint;
        _udpClient = new UdpClient();
        _receiveSource = new CancellationTokenSource();
        _isConnectionActive = true;
        _lastPingUtc = DateTime.UtcNow;
        _ = ReceiveRelayPacketsAsync(_receiveSource.Token);
        SendRelayPacket(new RelayPacket { Type = "Hello", ServerVersion = _serverVersion, ProtocolVersion = _protocolVersion, PersistentPlayerId = RefusionIdentity.PlayerId, PlatformId = PlayerIDManager.LocalPlatformID, Name = LocalPlayer.Username });
    }

    private async Task ReceiveRelayPacketsAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _udpClient != null)
            {
                var _result = await _udpClient.ReceiveAsync(token);
                var _packet = JsonSerializer.Deserialize<RelayPacket>(_result.Buffer, _jsonOptions);
                if (_packet != null) _receivedPackets.Enqueue(_packet);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception _exception) { FusionLogger.LogException("receiving dedicated server data", _exception); }
    }

    private void HandleRelayPacket(RelayPacket packet)
    {
        if (packet.Type.Equals("Reject", StringComparison.OrdinalIgnoreCase))
        {
            var _reason = packet.Reason ?? "Dedicated server rejected the connection.";
            if (string.Equals(_reason, "version_mismatch", StringComparison.OrdinalIgnoreCase)) _reason = $"Version mismatch. Client version: {_serverVersion}. Server version: {packet.ServerVersion ?? "unknown"}.";
            else if (string.Equals(_reason, "invalid_client", StringComparison.OrdinalIgnoreCase)) _reason = "The connection to the dedicated server was lost.";
            else if (string.Equals(_reason, "duplicate_player_identity", StringComparison.OrdinalIgnoreCase)) _reason = "Your identity is still connected. Wait a few seconds and try again.";
            else if (string.Equals(_reason, "invalid_persistent_player_id", StringComparison.OrdinalIgnoreCase)) _reason = "The server could not read your player identity.";
            Disconnect(_reason);
            return;
        }
        if (packet.Type.Equals("Welcome", StringComparison.OrdinalIgnoreCase))
        {
            _clientId = packet.ClientId;
            _smallId = packet.SmallId;
            _ownerClientId = packet.OwnerClientId;
            _sessionToken = packet.SessionToken;
            _isServerActive = _smallId == 0;
            _isRelayOperator = packet.IsOperator;
            PlayerIDManager.SetLongID(packet.PlatformId == 0 ? PlayerIDManager.LocalPlatformID : packet.PlatformId);
            _relaySmallIds[PlayerIDManager.LocalPlatformID] = _smallId;
            _relayOperators[PlayerIDManager.LocalPlatformID] = packet.IsOperator;
            _relayPersistentIds[PlayerIDManager.LocalPlatformID] = packet.PersistentPlayerId;
            if (_isServerActive) { InternalServerHelpers.OnStartServer(); RefreshServerCode(); }
            else ConnectionSender.SendConnectionRequest();
            return;
        }
        if (packet.Type.Equals("SettingsState", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(packet.SettingsJson)) return;
            try
            {
                var _info = JsonSerializer.Deserialize<LabFusion.Data.LobbyInfo>(packet.SettingsJson, _jsonOptions);
                if (_info != null) LobbyInfoManager.LobbyInfo = _info;
            }
            catch (Exception _exception) { FusionLogger.LogException("reading dedicated server settings", _exception); }
            return;
        }
        if (packet.Type.Equals("PermissionState", StringComparison.OrdinalIgnoreCase))
        {
            _relayOperators[packet.PlatformId] = packet.IsOperator;
            return;
        }
        if (packet.Type.Equals("Motd", StringComparison.OrdinalIgnoreCase) || packet.Type.Equals("Chat", StringComparison.OrdinalIgnoreCase))
        {
            Notifier.Send(new Notification { Title = packet.Type.Equals("Motd", StringComparison.OrdinalIgnoreCase) ? "Server MOTD" : packet.Name ?? "Server", Message = packet.Reason ?? string.Empty, PopupLength = 5f, ShowPopup = true, SaveToMenu = true, Type = NotificationType.INFORMATION });
            return;
        }
        if (packet.Type.Equals("SceneState", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(packet.LevelBarcode))
            {
                FusionSceneManager.SetTargetScene(packet.LevelBarcode, packet.LoadingScreenBarcode ?? string.Empty);
            }
            return;
        }
        if (packet.Type.Equals("ServerMessage", StringComparison.OrdinalIgnoreCase) || packet.Type.Equals("ClientMessage", StringComparison.OrdinalIgnoreCase) || packet.Type.Equals("RawForward", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryDecodePayload(packet.Payload, out var _payload)) return;
            if (packet.Type.Equals("ServerMessage", StringComparison.OrdinalIgnoreCase) && packet.PlatformId != 0) _relaySmallIds[packet.PlatformId] = packet.SmallId;
            if (packet.PlatformId != 0) _relayOperators[packet.PlatformId] = packet.IsOperator;
            if (packet.PlatformId != 0 && !string.IsNullOrWhiteSpace(packet.PersistentPlayerId)) _relayPersistentIds[packet.PlatformId] = packet.PersistentPlayerId;
            SteamSocketHandler.OnSocketMessageReceived(_payload, packet.IsServerHandled, packet.PlatformId);
            return;
        }
        if (packet.Type.Equals("PlayerJoined", StringComparison.OrdinalIgnoreCase))
        {
            if (packet.PlatformId != 0) _relaySmallIds[packet.PlatformId] = packet.SmallId;
            if (packet.PlatformId != 0) _relayOperators[packet.PlatformId] = packet.IsOperator;
            if (packet.PlatformId != 0 && !string.IsNullOrWhiteSpace(packet.PersistentPlayerId)) _relayPersistentIds[packet.PlatformId] = packet.PersistentPlayerId;
            return;
        }
        if (packet.Type.Equals("PlayerLeft", StringComparison.OrdinalIgnoreCase) && packet.PlatformId != 0)
        {
            _relaySmallIds.TryRemove(packet.PlatformId, out _);
            _relayOperators.TryRemove(packet.PlatformId, out _);
            _relayPersistentIds.TryRemove(packet.PlatformId, out _);
            InternalServerHelpers.OnPlayerLeft(packet.PlatformId);
            return;
        }
        if (packet.Type.Equals("SessionReset", StringComparison.OrdinalIgnoreCase)) { Disconnect("The dedicated server session was reset."); }
        if (packet.Type.Equals("ServerCommand", StringComparison.OrdinalIgnoreCase))
        {
            if (IsHost) _serverCommands.Enqueue(packet);
            return;
        }
    }

    private void ExecuteServerCommand(RelayPacket packet)
    {
        var _command = packet.Command?.Trim().ToLowerInvariant();
        var _arguments = packet.CommandArgs?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_command)) return;
        try
        {
            switch (_command)
            {
                case "clear":
                case "despawn":
                    PooleeUtilities.DespawnAll();
                    SendServerCommandResult(packet, "Scene objects cleared.");
                    break;
                case "map":
                case "load":
                    if (string.IsNullOrWhiteSpace(_arguments))
                    {
                        SendServerCommandResult(packet, "Usage: /map <level barcode>.");
                        break;
                    }
                    FusionSceneManager.SetTargetScene(_arguments, string.Empty);
                    FusionSceneManager.LoadTargetScene();
                    SendServerCommandResult(packet, $"Loading level {_arguments}.");
                    break;
                case "gamemode":
                    if (string.Equals(_arguments, "none", StringComparison.OrdinalIgnoreCase) || string.Equals(_arguments, "sandbox", StringComparison.OrdinalIgnoreCase))
                    {
                        GamemodeManager.DeselectGamemode();
                        SendServerCommandResult(packet, "Gamemode deselected.");
                        break;
                    }
                    if (!GamemodeRegistration.TryGetGamemode(_arguments, out var _gamemode))
                    {
                        SendServerCommandResult(packet, $"Gamemode not found: {_arguments}.");
                        break;
                    }
                    GamemodeManager.SelectGamemode(_gamemode);
                    SendServerCommandResult(packet, $"Gamemode selected: {_gamemode.Title}.");
                    break;
                case "gamemodes":
                    SendServerCommandResult(packet, string.Join(", ", GamemodeManager.Gamemodes.Select(_gamemode => $"{_gamemode.Title} ({_gamemode.Barcode})")));
                    break;
                default:
                    SendServerCommandResult(packet, $"Command is not implemented yet: /{_command}.");
                    break;
            }
        }
        catch (Exception _exception)
        {
            FusionLogger.LogException($"executing relay command {_command}", _exception);
            SendServerCommandResult(packet, $"Command failed: {_exception.Message}");
        }
    }

    private void SendServerCommandResult(RelayPacket packet, string message)
    {
        SendRelayPacket(new RelayPacket { Type = "ServerCommandResult", ClientId = _clientId, RequestId = packet.RequestId, Reason = message });
    }

    private void SendRelayPacket(RelayPacket packet)
    {
        if (_udpClient == null || _relayEndpoint == null) return;
        try
        {
            packet.SessionToken = _sessionToken;
            var _bytes = JsonSerializer.SerializeToUtf8Bytes(packet, _jsonOptions);
            _udpClient.Send(_bytes, _bytes.Length, _relayEndpoint);
        }
        catch (Exception _exception) { FusionLogger.LogException("sending dedicated server data", _exception); }
    }

    private RelayPacket CreateForwardPacket(string mode, NetworkChannel channel, NetMessage message, byte targetSmallId = 0, byte excludeSmallId = 0)
    {
        return new RelayPacket { Type = "RawForward", ClientId = _clientId, Mode = mode, Channel = (int)channel, TargetSmallId = targetSmallId, ExcludeSmallId = excludeSmallId, Payload = Convert.ToBase64String(message.ToByteArray()) };
    }

    private static bool TryDecodePayload(string payload, out byte[] bytes)
    {
        try { bytes = Convert.FromBase64String(payload ?? string.Empty); return bytes.Length > 0; }
        catch { bytes = Array.Empty<byte>(); return false; }
    }

    private static bool TryParseAddress(string address, out IPEndPoint endpoint)
    {
        endpoint = null;
        var _parts = address.Split(':', 2, StringSplitOptions.TrimEntries);
        return _parts.Length == 2 && IPAddress.TryParse(_parts[0], out var _ip) && int.TryParse(_parts[1], out var _port) && _port is > 0 and <= 65535 && (endpoint = new IPEndPoint(_ip, _port)) != null;
    }

    private static ulong GetPlatformId()
    {
        var _hash = SHA256.HashData(Guid.Parse(RefusionIdentity.PlayerId).ToByteArray());
        var _id = BitConverter.ToUInt64(_hash, 0);
        return _id == 0 ? 1 : _id;
    }

    public static bool TryGetRelaySmallId(ulong platformId, out byte smallId) => _relaySmallIds.TryGetValue(platformId, out smallId);
    public static bool IsRelayOperator(ulong platformId) => _relayOperators.TryGetValue(platformId, out var _isOperator) && _isOperator;
    public static bool TryGetRelayPersistentId(ulong platformId, out string persistentPlayerId) => _relayPersistentIds.TryGetValue(platformId, out persistentPlayerId);

    private sealed class RelayPacket
    {
        public string Type { get; set; } = string.Empty;
        public int ClientId { get; set; }
        public byte SmallId { get; set; }
        public int OwnerClientId { get; set; }
        public int ProtocolVersion { get; set; }
        public ulong PlatformId { get; set; }
        public string PersistentPlayerId { get; set; } = string.Empty;
        public string SessionToken { get; set; } = string.Empty;
        public string ServerVersion { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string SettingsJson { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public int Channel { get; set; }
        public byte TargetSmallId { get; set; }
        public byte ExcludeSmallId { get; set; }
        public bool IsServerHandled { get; set; }
        public bool IsOperator { get; set; }
        public string LevelBarcode { get; set; } = string.Empty;
        public string LoadingScreenBarcode { get; set; } = string.Empty;
        public bool IsLoading { get; set; }
        public string Command { get; set; } = string.Empty;
        public string CommandArgs { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
    }

    private void HookSteamEvents()
    {
        // Add server hooks
        MultiplayerHooking.OnPlayerJoined += OnPlayerJoin;
        MultiplayerHooking.OnPlayerLeft += OnPlayerLeave;
        MultiplayerHooking.OnDisconnected += OnDisconnect;

    }

    private void OnPlayerJoin(PlayerID id)
    {
        if (VoiceManager == null)
        {
            return;
        }

        if (!id.IsMe)
        {
            VoiceManager.GetSpeaker(id);
        }
    }

    private void OnPlayerLeave(PlayerID id)
    {
        if (VoiceManager == null)
        {
            return;
        }

        VoiceManager.RemoveSpeaker(id);
    }

    private void OnDisconnect()
    {
        if (VoiceManager == null)
        {
            return;
        }

        VoiceManager.ClearManager();
    }

    private void UnHookSteamEvents()
    {
        // Remove server hooks
        MultiplayerHooking.OnPlayerJoined -= OnPlayerJoin;
        MultiplayerHooking.OnPlayerLeft -= OnPlayerLeave;
        MultiplayerHooking.OnDisconnected -= OnDisconnect;

    }
}
