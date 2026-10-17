using Refusion.Transport;
using System.Diagnostics;
using LabFusion.Data;
using LabFusion.Player;
using LabFusion.Utilities;
using LabFusion.UI.Popups;

using LabFusion.Senders;
using LabFusion.Voice;
using LabFusion.Voice.Unity;
using LabFusion.Scene;
using LabFusion.SDK.Gamemodes;
using Il2CppSLZ.Marrow.Warehouse;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace LabFusion.Network;

public class DedicatedServerNetworkLayer : NetworkLayer
{
    public override string Title => "Dedicated Server";

    public override string Platform => "DedicatedServer";

    public override bool IsHost => _isServerActive;
    public override bool ServerCanSendToHost => false;
    public Guid SessionEpoch { get; private set; }
    public uint SceneEpoch { get; private set; }
    public int ConnectionGeneration => _joinAttempt;
    private bool _serverLoading;
    public long GetReliableBacklog() => _transport?.GetPendingBytes() ?? 0;
    public override bool IsClient => _isConnectionActive;

    private INetworkLobby _currentLobby;
    public override INetworkLobby Lobby => _currentLobby;

    private IVoiceManager _voiceManager = null;
    public override IVoiceManager VoiceManager => _voiceManager;

    private IMatchmaker _matchmaker = null;
    public override IMatchmaker Matchmaker => _matchmaker;

    protected bool _isServerActive = false;
    protected bool _isConnectionActive = false;
    public bool RelayOperator => _isRelayOperator;
    private RelayTransport _transport;
    private IPEndPoint _relayEndpoint;
    private string _serverAddress = string.Empty;
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private int _joinAttempt;
    private int _clientId;
    private byte _smallId;
    private int _ownerClientId;
    private bool _isRelayOperator;
    private string _sessionToken = string.Empty;
    private DateTime _lastPingUtc;
    private DateTime _joinDeadlineUtc = DateTime.MinValue;
    private DateTime _lastFusionJoinRetryUtc = DateTime.MinValue;
    private DateTime _fusionJoinDeadlineUtc = DateTime.MinValue;
    private DateTime _lastOverflowWarnUtc = DateTime.MinValue;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentQueue<RelayPacket> _serverCommands = new();
    private static readonly ConcurrentDictionary<ulong, byte> _relaySmallIds = new();
    private static readonly ConcurrentDictionary<ulong, bool> _relayOperators = new();
    private static readonly ConcurrentDictionary<ulong, string> _relayPersistentIds = new();

    private const string _serverVersion = ProductVersion.Value;
    private const int _protocolVersion = WireProtocol.Version;
    private const int _receivedPacketLimit = 1024;
    private const int _receivedPacketDrainBudget = 256;
    private static readonly TimeSpan _joinTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _fusionJoinTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _fusionJoinRetryInterval = TimeSpan.FromSeconds(2);

    public override bool CheckSupported()
    {
        return true;
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
        HookEvents();

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

        _currentLobby = null;

        Disconnect();

        UnhookEvents();
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

    public override void OnUpdateLayer()
    {
        while (_mainThreadActions.TryDequeue(out var _action))
        {
            try { _action(); }
            catch (Exception _exception) { FusionLogger.LogException("executing a queued relay action", _exception); }
        }
        if (_transport == null || !_isConnectionActive) return;
        if (_clientId == 0 && DateTime.UtcNow > _joinDeadlineUtc)
        {
            Disconnect("The server did not respond.");
            return;
        }
        if (_fusionJoinDeadlineUtc != DateTime.MinValue)
        {
            if (PlayerIDManager.LocalID != null)
            {
                _fusionJoinDeadlineUtc = DateTime.MinValue;
            }
            else if (DateTime.UtcNow > _fusionJoinDeadlineUtc)
            {
                _fusionJoinDeadlineUtc = DateTime.MinValue;
                FusionLogger.Warn("The server did not complete the Fusion-level join before the timeout.");
                Disconnect("The server did not respond.");
                return;
            }
            else if (DateTime.UtcNow - _lastFusionJoinRetryUtc > _fusionJoinRetryInterval)
            {
                _lastFusionJoinRetryUtc = DateTime.UtcNow;
                SendConnectionRequestSafe();
            }
        }
        var _clock = Stopwatch.StartNew();
        for (var _i = 0; _i < _receivedPacketDrainBudget && _clock.Elapsed.TotalMilliseconds < 2 && _transport != null && _transport.GetTryReceive(out var _received); _i++)
        {
            if (_received.Local) { Disconnect(_received.Packet.Reason); break; }
            HandleRelayPacket(_received.Packet);
        }
        WorldCatchup.SetUpdate();
        while (_serverCommands.TryDequeue(out var _command)) ExecuteServerCommand(_command);
        if (DateTime.UtcNow - _lastPingUtc > TimeSpan.FromSeconds(5))
        {
            _lastPingUtc = DateTime.UtcNow;
            SendRelayPacket(new RelayPacket { Type = "Ping", ClientId = _clientId });
        }
    }

    private void SendConnectionRequestSafe()
    {
        try
        {
            if (!NetworkInfo.HasServer || _isServerActive || _serverLoading) return;
            ConnectionSender.SendConnectionRequest();
#if DEBUG
            FusionLogger.Log("Sent a connection request to the dedicated server.");
#endif
        }
        catch (Exception _exception) { FusionLogger.LogException("sending a connection request", _exception); }
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
        if (!IsHost) return;
        if (isLoading) { SceneEpoch++; WorldCatchup.SetReset(); }
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
        else if (CanSendGameMessage(message))
        {
            SendRelayPacket(CreateForwardPacket("ClientMessage", channel, message));
        }
    }

    public override void BroadcastMessageExcept(byte userId, NetworkChannel channel, NetMessage message, bool ignoreHost = true)
    {
        if (IsHost) SendRelayPacket(CreateForwardPacket("ServerBroadcastExcept", channel, message, excludeSmallId: userId));
    }

    public override void BroadcastMessageExcept(ulong userId, NetworkChannel channel, NetMessage message, bool ignoreHost = true)
    {
        var _id = PlayerIDManager.GetPlayerID(userId);
        if (_id != null) BroadcastMessageExcept(_id.SmallID, channel, message, ignoreHost);
    }

    public override void SendToServer(NetworkChannel channel, NetMessage message)
    {
        if (CanSendGameMessage(message))
        {
            SendRelayPacket(CreateForwardPacket("ClientMessage", channel, message));
        }
    }

    private unsafe bool CanSendGameMessage(NetMessage message)
    {
        // Until our own connection response is processed, only the join handshake may go out.
        // Anything else carries sender id 0 and looks like spoofing to the host.
        if (!_isConnectionActive || PlayerIDManager.LocalID != null) return true;
        if (message.Length > 0 && message.Buffer[0] == NativeMessageTag.ConnectionRequest) return true;
#if DEBUG
        FusionLogger.Log("Blocked a game message while waiting for the Fusion-level join to complete.");
#endif
        return false;
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
        if (_target != null)
        {
            if (_target.IsMe) { RelayMessageDispatcher.Dispatch(message.ToByteArray(), false, PlayerIDManager.LocalPlatformID); return; }
            SendRelayPacket(CreateForwardPacket("ServerTarget", channel, message, _target.SmallID));
            return;
        }

        // A player that just sent a connection request has no PlayerID inserted on the host yet, so resolve through the relay identity map
        if (TryGetRelaySmallId(userId, out var _relaySmallId))
        {
            SendRelayPacket(CreateForwardPacket("ServerTarget", channel, message, _relaySmallId));
        }
    }

    public override void StartServer()
    {
        Notifier.Send(new Notification { Title = "Unable to Create Server", Message = "Manual server creation is disabled. Enter a relay address to join a dedicated server.", PopupLength = 5f, ShowPopup = true, Type = NotificationType.ERROR });
    }

    public override void Disconnect(string reason = "")
    {
        _joinAttempt++;

        // Make sure we are currently in a server
        if (!_isServerActive && !_isConnectionActive)
            return;

        try
        {
            if (_clientId != 0) SendRelayPacket(new RelayPacket { Type = "Leave", ClientId = _clientId });
            _transport?.SetDisconnect(_relayEndpoint, "leave");
            _transport?.Dispose();
        }
        catch
        {
            FusionLogger.Log("Error closing socket server / connection manager");
        }

        _isServerActive = false;
        _isConnectionActive = false;
        _transport = null;
        SessionEpoch = Guid.Empty;
        SceneEpoch = 0;
        WorldCatchup.SetReset();
        while (_serverCommands.TryDequeue(out _)) { }
        while (_mainThreadActions.TryDequeue(out _)) { }
        _clientId = 0;
        _smallId = 0;
        _ownerClientId = 0;
        _isRelayOperator = false;
        _sessionToken = string.Empty;
        _joinDeadlineUtc = DateTime.MinValue;
        _lastPingUtc = DateTime.MinValue;
        _lastFusionJoinRetryUtc = DateTime.MinValue;
        _fusionJoinDeadlineUtc = DateTime.MinValue;
        _serverAddress = string.Empty;
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

    public string GetServerAddress()
    {
        return _serverAddress;
    }

    public override void JoinServerByAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        DedicatedServerHistory.Add(address);
        JoinDedicatedServer(address);
    }

    protected virtual void JoinDedicatedServer(string address) => JoinDedicatedServerInternal(address);

    protected void JoinDedicatedServerInternal(string address)
    {
        var _attempt = ++_joinAttempt;

        if (!TryParseAddress(address, out var _host, out var _port))
        {
            FusionLogger.Error($"Invalid dedicated server address: {address}");
            Notifier.Send(new Notification { Title = "Invalid Server Address", Message = $"\"{address}\" is not a valid address. Enter an IP or hostname with a port, e.g. 127.0.0.1:28430.", PopupLength = 5f, ShowPopup = true, Type = NotificationType.ERROR });
            return;
        }

        if (IPAddress.TryParse(_host, out var _ip))
        {
            ConnectToRelay(address, new IPEndPoint(_ip, _port));
            return;
        }

        Task.Run(async () =>
        {
            IPAddress _resolved = null;
            try
            {
                var _resolveTask = Dns.GetHostAddressesAsync(_host);
                var _completedTask = await Task.WhenAny(_resolveTask, Task.Delay(_joinTimeout));
                if (_completedTask == _resolveTask)
                {
                    var _addresses = await _resolveTask;
                    _resolved = _addresses.FirstOrDefault(_entry => _entry.AddressFamily == AddressFamily.InterNetwork) ?? _addresses.FirstOrDefault();
                }
                else
                {
                    FusionLogger.Warn($"Resolving relay host {_host} timed out.");
                }
            }
            catch (Exception _exception) { FusionLogger.Warn($"Could not resolve relay host {_host}: {_exception.Message}"); }

            _mainThreadActions.Enqueue(() =>
            {
                if (_attempt != _joinAttempt) return;

                if (_resolved == null)
                {
                    Notifier.Send(new Notification { Title = "Failed to Resolve Address", Message = $"Could not resolve \"{_host}\".", PopupLength = 5f, ShowPopup = true, Type = NotificationType.ERROR });
                    return;
                }

                ConnectToRelay(address, new IPEndPoint(_resolved, _port));
            });
        });
    }

    private void ConnectToRelay(string address, IPEndPoint endpoint)
    {
        if (_isConnectionActive || _isServerActive) Disconnect();
        _serverAddress = address;
        _relayEndpoint = endpoint;
        _transport = new RelayTransport();
        _transport.SetConnect(endpoint);
        _isConnectionActive = true;
        _lastPingUtc = DateTime.UtcNow;
        _joinDeadlineUtc = DateTime.UtcNow + _joinTimeout;
        _lastFusionJoinRetryUtc = DateTime.MinValue;
        _fusionJoinDeadlineUtc = DateTime.MinValue;
        SendRelayPacket(new RelayPacket { Type = "Hello", ServerVersion = _serverVersion, ProtocolVersion = _protocolVersion, PersistentPlayerId = RefusionIdentity.PlayerId, PlatformId = PlayerIDManager.LocalPlatformID, Name = LocalPlayer.Username });
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
            if (_clientId != 0) return;
            if (packet.ProtocolVersion != _protocolVersion || packet.ServerVersion != _serverVersion) { Disconnect("Version mismatch: update both client and relay."); return; }
            SessionEpoch = packet.SessionEpoch;
            SceneEpoch = packet.SceneEpoch;
            _serverLoading = packet.IsLoading;
            _transport.SetBudget(_relayEndpoint, packet.SmallId == 0 ? packet.HostBytesPerSecond : packet.ClientBytesPerSecond);
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
            if (_isServerActive) { InternalServerHelpers.OnStartServer(); }
            else
            {
                _lastFusionJoinRetryUtc = DateTime.UtcNow;
                _fusionJoinDeadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(180);
                SendConnectionRequestSafe();
            }
            return;
        }
        if (packet.SessionEpoch != SessionEpoch) return;
        if (packet.Type == "LoadingState") { _serverLoading = packet.IsLoading; return; }
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
        if (packet.Type == "SceneState") { _serverLoading = packet.IsLoading; return; }
        if (packet.Type.Equals("ServerMessage", StringComparison.OrdinalIgnoreCase) || packet.Type.Equals("ClientMessage", StringComparison.OrdinalIgnoreCase) || packet.Type.Equals("RawForward", StringComparison.OrdinalIgnoreCase))
        {
            var _payload = packet.Payload;
            if (!FusionEnvelope.GetTryParse(_payload, out var _prefix)) return;
            if (_prefix.Tag == NativeMessageTag.SceneLoad && packet.SceneEpoch > SceneEpoch) { SceneEpoch = packet.SceneEpoch; WorldCatchup.SetReset(); }
            if (!FusionEnvelope.GetIsSessionMessage(_prefix.Tag) && packet.SceneEpoch != SceneEpoch) return;
            if (IsHost && WorldCatchup.GetHasSnapshots() && WorldCatchup.GetTryBuffer(packet.SmallId, packet)) return;
            if (packet.Type.Equals("ServerMessage", StringComparison.OrdinalIgnoreCase) && packet.PlatformId != 0) _relaySmallIds[packet.PlatformId] = packet.SmallId;
            if (packet.PlatformId != 0) _relayOperators[packet.PlatformId] = packet.IsOperator;
            if (packet.PlatformId != 0 && !string.IsNullOrWhiteSpace(packet.PersistentPlayerId)) _relayPersistentIds[packet.PlatformId] = packet.PersistentPlayerId;
            RelayMessageDispatcher.Dispatch(_payload, packet.IsServerHandled, packet.PlatformId, packet.SceneEpoch, packet.Sequence, packet.CatchupId);
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
        if (_transport == null || _relayEndpoint == null) return;
        try
        {
            packet.SessionToken = _sessionToken;
            packet.SessionEpoch = SessionEpoch;
            packet.SceneEpoch = packet.Payload != null && packet.SceneEpoch != 0 ? packet.SceneEpoch : SceneEpoch;
            _transport.SetSend(_relayEndpoint, packet);
        }
        catch (Exception _exception) { FusionLogger.LogException("sending dedicated server data", _exception); Disconnect("Transport send failed: " + _exception.Message); }
    }

#if DEBUG
    internal static DateTime LastJoinSendUtc = DateTime.MinValue;
#endif

    private RelayPacket CreateForwardPacket(string mode, NetworkChannel channel, NetMessage message, byte targetSmallId = 0, byte excludeSmallId = 0)
    {
        return new RelayPacket { Type = "RawForward", ClientId = _clientId, Mode = mode, Channel = (int)channel, TargetSmallId = targetSmallId, ExcludeSmallId = excludeSmallId, Payload = message.ToByteArray(), SceneEpoch = message.SceneEpoch, Sequence = message.TransportSequence, CatchupId = message.CatchupId != 0 ? message.CatchupId : WorldCatchup.ContextId };
    }

    public void SetJoinStage(string stage, uint catchupId, int missing)
    {
        SendRelayPacket(new RelayPacket { Type = "JoinState", ClientId = _clientId, JoinStage = stage, CatchupId = catchupId, MissingCount = missing });
    }

    public void SetSendBuffered(RelayPacket packet)
    {
        RelayMessageDispatcher.Dispatch(packet.Payload, packet.IsServerHandled, packet.PlatformId, packet.SceneEpoch, packet.Sequence, packet.CatchupId);
    }

    private static bool TryParseAddress(string address, out string host, out int port)
    {
        host = null;
        port = 0;
        var _parts = address.Split(':', 2, StringSplitOptions.TrimEntries);
        if (_parts.Length != 2 || string.IsNullOrWhiteSpace(_parts[0])) return false;
        if (!int.TryParse(_parts[1], out port) || port is < 1 or > 65535) return false;
        var _hostText = _parts[0];
        if (IPAddress.TryParse(_hostText, out var _ip))
        {
            if (!string.Equals(_ip.ToString(), _hostText, StringComparison.Ordinal)) return false;
        }
        else if (Uri.CheckHostName(_hostText) == UriHostNameType.Unknown || !HasLetter(_hostText))
        {
            return false;
        }
        host = _hostText;
        return true;
    }

    private static bool HasLetter(string text)
    {
        foreach (var _character in text)
        {
            if (char.IsLetter(_character)) return true;
        }
        return false;
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

    private void HookEvents()
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

    private void UnhookEvents()
    {
        // Remove server hooks
        MultiplayerHooking.OnPlayerJoined -= OnPlayerJoin;
        MultiplayerHooking.OnPlayerLeft -= OnPlayerLeave;
        MultiplayerHooking.OnDisconnected -= OnDisconnect;

    }
}
