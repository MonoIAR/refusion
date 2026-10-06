using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace RefusionRelay;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var _port = GetPort(args);
        var _server = new RelayServer(_port);
        Console.CancelKeyPress += (_, _event) =>
        {
            _event.Cancel = true;
            _server.Stop();
        };
        await _server.RunAsync();
    }

    private static int GetPort(string[] args)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out var _port) || _port is < 1 or > 65535) return 28430;
        return _port;
    }
}

internal sealed class RelayServer
{
    private const string _serverVersion = "0.1.0";
    private const int _protocolVersion = 1;
    private static readonly TimeSpan _clientTimeout = TimeSpan.FromSeconds(15);
    private const string _stateFileName = "refusion-server-state.json";
    private const string _settingsFileName = "refusion-server-settings.json";
    private const string _accessFileName = "refusion-server-access.json";
    private readonly string _logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private readonly UdpClient _socket;
    private readonly ConcurrentDictionary<int, RelayClient> _clients = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _serverId;
    private readonly RelaySettings _settings;
    private readonly RelayAccessList _accessList;
    private int _nextClientId;
    private byte _nextSmallId = 1;
    private int? _ownerClientId;
    private CancellationTokenSource _stopSource = new();
    private string _levelBarcode = string.Empty;
    private string _loadingScreenBarcode = string.Empty;
    private string _settingsJson = string.Empty;
    private string _motd = string.Empty;
    private bool _isLoading;

    public RelayServer(int port)
    {
        _socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        _settings = LoadSettings();
        _accessList = LoadAccessList();
        var _state = LoadState();
        _serverId = string.IsNullOrWhiteSpace(_state.ServerId) ? Guid.NewGuid().ToString("N") : _state.ServerId;
        _levelBarcode = _state.LevelBarcode ?? string.Empty;
        _loadingScreenBarcode = _state.LoadingScreenBarcode ?? string.Empty;
        _settingsJson = _state.SettingsJson ?? string.Empty;
        _motd = _state.Motd ?? string.Empty;
        SaveState();
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(_logDirectory);
        PrintBanner();
        Log("INFO", $"Version: {_serverVersion}");
        Log("INFO", $"Protocol: {_protocolVersion}");
        Log("INFO", $"Server ID: {_serverId}");
        Log("INFO", $"Listening: {_socket.Client.LocalEndPoint}");
        Log("INFO", $"Log file: {GetLogPath()}");
        var _cleanupTask = CleanupAsync(_stopSource.Token);
        var _consoleTask = ReadConsoleAsync(_stopSource.Token);
        try
        {
            while (!_stopSource.IsCancellationRequested)
            {
                UdpReceiveResult _packet;
                try { _packet = await _socket.ReceiveAsync(_stopSource.Token); }
                catch (OperationCanceledException) { break; }
                try { await HandlePacketAsync(_packet); }
                catch (Exception _exception) { Log("ERROR", $"Packet handling failed: {_exception}"); }
            }
        }
        finally
        {
            _stopSource.Cancel();
            try { await _cleanupTask; } catch (OperationCanceledException) { }
            try { await _consoleTask; } catch (OperationCanceledException) { }
            _socket.Dispose();
        }
    }

    private void PrintBanner()
    {
        var _bannerPath = Path.Combine(AppContext.BaseDirectory, "banner.txt");
        if (!File.Exists(_bannerPath)) return;
        Console.WriteLine(File.ReadAllText(_bannerPath).TrimEnd());
    }

    public void Stop()
    {
        if (_stopSource.IsCancellationRequested) return;
        _stopSource.Cancel();
        _socket.Close();
    }

    private async Task HandlePacketAsync(UdpReceiveResult packet)
    {
        RelayPacket? _message;
        try { _message = JsonSerializer.Deserialize<RelayPacket>(packet.Buffer, _jsonOptions); }
        catch (JsonException) { Console.WriteLine("Received malformed JSON packet."); return; }
        if (_message is null || string.IsNullOrWhiteSpace(_message.Type)) return;

        if (_message.Type.Equals("Hello", StringComparison.OrdinalIgnoreCase))
        {
            await HandleHelloAsync(packet.RemoteEndPoint, _message);
            return;
        }

        if (_message.Type.Equals("Query", StringComparison.OrdinalIgnoreCase))
        {
            await SendAsync(packet.RemoteEndPoint, CreateQueryPacket());
            return;
        }

        if (!_clients.TryGetValue(_message.ClientId, out var _client) || !_client.EndPoint.Equals(packet.RemoteEndPoint))
        {
            await SendAsync(packet.RemoteEndPoint, new RelayPacket { Type = "Reject", Reason = "invalid_client" });
            return;
        }

        _client.LastSeenUtc = DateTime.UtcNow;
        if (_message.Type.Equals("Ping", StringComparison.OrdinalIgnoreCase))
        {
            await SendAsync(_client.EndPoint, CreateStatePacket("Pong", _client));
            return;
        }

        if (_message.Type.Equals("RawForward", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRawForwardAsync(_client, _message);
            return;
        }

        if (_message.Type.Equals("Kick", StringComparison.OrdinalIgnoreCase))
        {
            await HandleKickRequestAsync(_client, _message);
            return;
        }
        if (_message.Type.Equals("Ban", StringComparison.OrdinalIgnoreCase))
        {
            await HandleBanRequestAsync(_client, _message);
            return;
        }
        if (_message.Type.Equals("Unban", StringComparison.OrdinalIgnoreCase))
        {
            await HandleUnbanRequestAsync(_client, _message);
            return;
        }
        if (_message.Type.Equals("ServerCommandResult", StringComparison.OrdinalIgnoreCase))
        {
            if (_client.ClientId == _ownerClientId) Log("INFO", $"Command result: {_message.Reason}");
            return;
        }

        if (_message.Type.Equals("Leave", StringComparison.OrdinalIgnoreCase)) await RemoveClientAsync(_client, true);
    }

    private async Task HandleHelloAsync(IPEndPoint endpoint, RelayPacket message)
    {
        if (!string.Equals(message.ServerVersion, _serverVersion, StringComparison.Ordinal) || message.ProtocolVersion != _protocolVersion)
        {
            await SendAsync(endpoint, new RelayPacket { Type = "Reject", Reason = "version_mismatch", ServerVersion = _serverVersion, ProtocolVersion = _protocolVersion });
            return;
        }
        if (_isLoading)
        {
            await SendAsync(endpoint, new RelayPacket { Type = "Reject", Reason = "The server is loading a level. Please try again later.", ServerVersion = _serverVersion, ProtocolVersion = _protocolVersion, ServerId = _serverId, AnticheatEnabled = _settings.AnticheatEnabled });
            return;
        }
        if (_clients.Count >= _settings.MaxPlayers)
        {
            await SendAsync(endpoint, new RelayPacket { Type = "Reject", Reason = "The server is full.", ServerVersion = _serverVersion, ProtocolVersion = _protocolVersion });
            return;
        }
        if (!Guid.TryParse(message.PersistentPlayerId, out var _persistentPlayerId))
        {
            await SendAsync(endpoint, new RelayPacket { Type = "Reject", Reason = "invalid_persistent_player_id", ServerVersion = _serverVersion, ProtocolVersion = _protocolVersion });
            return;
        }
        var _playerId = _persistentPlayerId.ToString("D");
        string? _banReason;
        lock (_gate) _banReason = _accessList.Bans.FirstOrDefault(_entry => _entry.PlayerId.Equals(_playerId, StringComparison.OrdinalIgnoreCase))?.Reason;
        if (_banReason is not null)
        {
            await SendAsync(endpoint, new RelayPacket { Type = "Reject", Reason = _banReason, ServerVersion = _serverVersion, ProtocolVersion = _protocolVersion });
            Log("WARN", $"Rejected banned player {_playerId} from {endpoint}: {_banReason}");
            return;
        }

        RelayClient? _client;
        var _created = false;
        lock (_gate)
        {
            if (_clients.Values.FirstOrDefault(_value => _value.EndPoint.Equals(endpoint)) is { } _existing)
            {
                if (!_existing.PersistentPlayerId.Equals(_playerId, StringComparison.OrdinalIgnoreCase) || (_existing.PlatformId != 0 && message.PlatformId != 0 && _existing.PlatformId != message.PlatformId))
                {
                    _client = null;
                }
                else
                {
                    _existing.LastSeenUtc = DateTime.UtcNow;
                    _client = _existing;
                }
            }
            else if (_clients.Values.Any(_value => _value.PersistentPlayerId.Equals(_playerId, StringComparison.OrdinalIgnoreCase) || (message.PlatformId != 0 && _value.PlatformId == message.PlatformId)))
            {
                _client = null;
            }
            else
            {
                var _clientId = Interlocked.Increment(ref _nextClientId);
                var _isOwner = _ownerClientId is null;
                _client = new RelayClient(_clientId, GetSmallId(_isOwner), endpoint, string.IsNullOrWhiteSpace(message.Name) ? $"Player{_clientId}" : message.Name, GetPlatformId(message.PlatformId, _playerId), _playerId);
                _clients[_client.ClientId] = _client;
                if (_isOwner) _ownerClientId = _client.ClientId;
                _created = true;
            }
        }
        if (_client is null)
        {
            await SendAsync(endpoint, new RelayPacket { Type = "Reject", Reason = "duplicate_player_identity", ServerVersion = _serverVersion, ProtocolVersion = _protocolVersion });
            Log("WARN", $"Rejected duplicate identity {_playerId} from {endpoint}");
            return;
        }

        await SendAsync(endpoint, new RelayPacket { Type = "Welcome", ClientId = _client.ClientId, SmallId = _client.SmallId, OwnerClientId = _ownerClientId ?? 0, ServerVersion = _serverVersion, ProtocolVersion = _protocolVersion, Name = _client.Name, PlatformId = _client.PlatformId, PersistentPlayerId = _client.PersistentPlayerId, IsOperator = IsOperator(_client.PersistentPlayerId) });
        if (_created) Log("INFO", $"Player joined: {_client.Name}, id={_client.SmallId}, uuid={_client.PersistentPlayerId}, endpoint={endpoint}");
        if (!string.IsNullOrWhiteSpace(_levelBarcode)) await SendAsync(endpoint, CreateStatePacket("SceneState", _client));
        if (!string.IsNullOrWhiteSpace(_settingsJson)) await SendAsync(endpoint, new RelayPacket { Type = "SettingsState", SettingsJson = _settingsJson });
        if (!string.IsNullOrWhiteSpace(_motd)) await SendAsync(endpoint, new RelayPacket { Type = "Motd", Reason = _motd });
        if (_created) await BroadcastAsync(_client, new RelayPacket { Type = "PlayerJoined", ClientId = _client.ClientId, SmallId = _client.SmallId, Name = _client.Name, OwnerClientId = _ownerClientId ?? 0, PlatformId = _client.PlatformId, PersistentPlayerId = _client.PersistentPlayerId, IsOperator = IsOperator(_client.PersistentPlayerId) });
    }

    private async Task HandleRawForwardAsync(RelayClient client, RelayPacket message)
    {
        if (client.ClientId == _ownerClientId && string.Equals(message.Mode, "ServerSceneState", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(message.LevelBarcode)) return;
            _levelBarcode = message.LevelBarcode;
            _loadingScreenBarcode = message.LoadingScreenBarcode ?? string.Empty;
            SaveState();
            await BroadcastAllAsync(CreateStatePacket("SceneState", client));
            return;
        }

        if (client.ClientId == _ownerClientId && string.Equals(message.Mode, "ServerLoadingState", StringComparison.OrdinalIgnoreCase))
        {
            _isLoading = message.IsLoading;
            SaveState();
            await BroadcastAllAsync(CreateStatePacket("LoadingState", client));
            return;
        }
        if (client.ClientId == _ownerClientId && string.Equals(message.Mode, "ServerSettingsState", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(message.SettingsJson)) return;
            _settingsJson = message.SettingsJson;
            SaveState();
            await BroadcastAllAsync(new RelayPacket { Type = "SettingsState", SettingsJson = _settingsJson });
            Log("INFO", "Server settings updated and broadcast.");
            return;
        }

        var _packet = new RelayPacket { Type = "RawForward", ClientId = client.ClientId, SmallId = client.SmallId, Payload = message.Payload, Channel = message.Channel, IsServerHandled = false, PlatformId = client.PlatformId };
        if (client.ClientId == _ownerClientId)
        {
            if (string.Equals(message.Mode, "ServerBroadcast", StringComparison.OrdinalIgnoreCase))
            {
                _packet.Type = "ClientMessage";
                _packet.IsServerHandled = false;
                await BroadcastExceptSmallIdAsync(client.SmallId, _packet);
                return;
            }
            if (string.Equals(message.Mode, "ServerTarget", StringComparison.OrdinalIgnoreCase))
            {
                await SendToSmallIdAsync(message.TargetSmallId, new RelayPacket { Type = "ClientMessage", ClientId = client.ClientId, SmallId = client.SmallId, Payload = message.Payload, Channel = message.Channel, IsServerHandled = false, PlatformId = client.PlatformId });
                return;
            }
            if (string.Equals(message.Mode, "ServerBroadcastExcept", StringComparison.OrdinalIgnoreCase))
            {
                await BroadcastExceptSmallIdAsync(message.ExcludeSmallId, new RelayPacket { Type = "ClientMessage", ClientId = client.ClientId, SmallId = client.SmallId, Payload = message.Payload, Channel = message.Channel, IsServerHandled = false, PlatformId = client.PlatformId });
                return;
            }
            return;
        }
        if (_ownerClientId is int _ownerId && _clients.TryGetValue(_ownerId, out var _owner)) await SendAsync(_owner.EndPoint, new RelayPacket { Type = "ServerMessage", ClientId = client.ClientId, SmallId = client.SmallId, Payload = message.Payload, Channel = message.Channel, IsServerHandled = true, PlatformId = client.PlatformId, IsOperator = IsOperator(client.PersistentPlayerId) });
    }

    private async Task HandleKickRequestAsync(RelayClient requester, RelayPacket message)
    {
        if (requester.ClientId != _ownerClientId && !IsOperator(requester.PersistentPlayerId))
        {
            Log("WARN", $"Rejected unauthorized kick request from {requester.Name} ({requester.PersistentPlayerId})");
            return;
        }
        var _target = message.TargetSmallId != 0
            ? _clients.Values.FirstOrDefault(_client => _client.SmallId == message.TargetSmallId)
            : !string.IsNullOrWhiteSpace(message.PersistentPlayerId)
                ? _clients.Values.FirstOrDefault(_client => _client.PersistentPlayerId.Equals(message.PersistentPlayerId, StringComparison.OrdinalIgnoreCase))
                : null;
        if (_target is null || _target.ClientId == requester.ClientId) return;
        var _reason = string.IsNullOrWhiteSpace(message.Reason) ? "Kicked from server." : message.Reason;
        await SendAsync(_target.EndPoint, new RelayPacket { Type = "Reject", Reason = _reason });
        Log("INFO", $"Player kicked: {_target.Name} ({_target.PersistentPlayerId}) by {requester.Name} ({requester.PersistentPlayerId}): {_reason}");
        await RemoveClientAsync(_target, true);
    }

    private async Task HandleBanRequestAsync(RelayClient requester, RelayPacket message)
    {
        if (requester.ClientId != _ownerClientId && !IsOperator(requester.PersistentPlayerId)) return;
        var _target = _clients.Values.FirstOrDefault(_client => _client.SmallId == message.TargetSmallId);
        if (_target is null || _target.ClientId == requester.ClientId) return;
        var _reason = string.IsNullOrWhiteSpace(message.Reason) ? "Banned by server administrator." : message.Reason;
        lock (_gate)
        {
            _accessList.Bans.RemoveAll(_entry => _entry.PlayerId.Equals(_target.PersistentPlayerId, StringComparison.OrdinalIgnoreCase));
            _accessList.Bans.Add(new RelayBanEntry { PlayerId = _target.PersistentPlayerId, Reason = _reason, CreatedUtc = DateTime.UtcNow });
        }
        SaveAccessList();
        await SendAsync(_target.EndPoint, new RelayPacket { Type = "Reject", Reason = _reason });
        await RemoveClientAsync(_target, true);
        Log("INFO", $"Player banned: {_target.Name} ({_target.PersistentPlayerId}): {_reason}");
    }

    private async Task HandleUnbanRequestAsync(RelayClient requester, RelayPacket message)
    {
        if (requester.ClientId != _ownerClientId && !IsOperator(requester.PersistentPlayerId)) return;
        if (string.IsNullOrWhiteSpace(message.PersistentPlayerId)) return;
        lock (_gate) _accessList.Bans.RemoveAll(_entry => _entry.PlayerId.Equals(message.PersistentPlayerId, StringComparison.OrdinalIgnoreCase));
        SaveAccessList();
        Log("INFO", $"Player unbanned: {message.PersistentPlayerId}");
        await Task.CompletedTask;
    }

    private byte GetSmallId(bool isOwner)
    {
        if (isOwner) return 0;
        for (var _offset = 0; _offset < 255; _offset++)
        {
            var _candidate = unchecked((byte)(_nextSmallId + _offset));
            if (_candidate == 0) continue;
            if (_clients.Values.All(_client => _client.SmallId != _candidate))
            {
                _nextSmallId = unchecked((byte)(_candidate + 1));
                return _candidate;
            }
        }
        throw new InvalidOperationException("No SmallID is available.");
    }

    private bool IsOperator(string playerId) { lock (_gate) return _accessList.Operators.Any(_entry => _entry.PlayerId.Equals(playerId, StringComparison.OrdinalIgnoreCase)); }

    private static ulong GetPlatformId(ulong platformId, string playerId)
    {
        if (platformId != 0) return platformId;
        var _bytes = SHA256.HashData(Guid.Parse(playerId).ToByteArray());
        var _id = BitConverter.ToUInt64(_bytes, 0);
        return _id == 0 ? 1 : _id;
    }

    private async Task CleanupAsync(CancellationToken token)
    {
        using var _timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await _timer.WaitForNextTickAsync(token))
        {
            foreach (var _client in _clients.Values)
            {
                if (DateTime.UtcNow - _client.LastSeenUtc > _clientTimeout) await RemoveClientAsync(_client, true);
            }
        }
    }

    private async Task ReadConsoleAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var _line = await Console.In.ReadLineAsync(token);
            if (_line is null) return;
            await HandleConsoleCommandAsync(_line);
        }
    }

    private async Task HandleConsoleCommandAsync(string line)
    {
        line = line.Trim();
        if (line.StartsWith('/')) line = line[1..].TrimStart();
        var _parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (_parts.Length == 0) return;
        var _command = _parts[0].ToLowerInvariant();
        if (_command is "help" or "?")
        {
            Console.WriteLine("Commands: help, ?, players, list, status, version, motd [text|none], say <text>, gamemodes, op <uuid>, deop <uuid>, ops, kick <uuid> [reason], ban <uuid> [reason], unban <uuid>, bans, banlist, maxplayers <1-255>, stop");
            return;
        }
        if (_command == "players")
        {
            foreach (var _client in _clients.Values.OrderBy(_value => _value.SmallId)) Console.WriteLine($"{_client.SmallId}: {_client.Name} ({_client.PersistentPlayerId})");
            return;
        }
        if (_command == "list") { await HandleConsoleCommandAsync("players"); return; }
        if (_command == "status")
        {
            Console.WriteLine($"Players: {_clients.Count}/255");
            Console.WriteLine($"Level: {_levelBarcode}");
            Console.WriteLine($"Gamemode: {_settings.GamemodeTitle ?? "Sandbox"}");
            Console.WriteLine($"MOTD: {_motd}");
            return;
        }
        if (_command == "version") { Console.WriteLine($"Refusion Relay {_serverVersion}, protocol {_protocolVersion}"); return; }
        if (_command == "stop") { Stop(); return; }
        if (_command is "gamemode" or "gamemodes")
        {
            if (_command == "gamemode" && _parts.Length > 1)
            {
                await SendServerCommandAsync("gamemode", _parts[1]);
                return;
            }
            if (_command == "gamemodes")
            {
                await SendServerCommandAsync("gamemodes", string.Empty);
                return;
            }
            Console.WriteLine($"Current gamemode: {_settings.GamemodeTitle ?? "Sandbox"} ({_settings.GamemodeBarcode ?? "none"})");
            Console.WriteLine("The complete gamemode catalog is provided by the connected Fusion client.");
            return;
        }
        if (_command is "clear" or "despawn")
        {
            await SendServerCommandAsync("clear", _parts.Length > 1 ? _parts[1] : string.Empty);
            return;
        }
        if (_command is "map" or "load")
        {
            if (_parts.Length < 2) { Console.WriteLine("Usage: map <level barcode>"); return; }
            await SendServerCommandAsync("map", _parts[1]);
            return;
        }
        if (_command is "time")
        {
            await SendServerCommandAsync("time", _parts.Length > 1 ? _parts[1] : string.Empty);
            return;
        }
        if (_command is "tp" or "teleport" or "heal" or "damage" or "restart" or "reload" or "snapshot" or "save" or "votecancel")
        {
            await SendServerCommandAsync(_command, _parts.Length > 1 ? line[(line.IndexOf(' ') + 1)..].Trim() : string.Empty);
            return;
        }
        if (_command is "motd" or "messageoftheday")
        {
            _motd = _parts.Length < 2 || string.Equals(_parts[1], "none", StringComparison.OrdinalIgnoreCase) ? string.Empty : line[(line.IndexOf(' ') + 1)..].Trim();
            SaveState();
            await BroadcastAllAsync(new RelayPacket { Type = "Motd", Reason = _motd });
            Log("INFO", $"MOTD updated: {_motd}");
            return;
        }
        if (_command is "say" or "broadcast")
        {
            if (_parts.Length < 2) return;
            var _text = line[(line.IndexOf(' ') + 1)..].Trim();
            await BroadcastAllAsync(new RelayPacket { Type = "Chat", Name = "Server", Reason = _text });
            Log("INFO", $"Server say: {_text}");
            return;
        }
        if (_command == "maxplayers")
        {
            if (_parts.Length < 2 || !int.TryParse(_parts[1], out var _maxPlayers) || _maxPlayers < 1 || _maxPlayers > 255) { Console.WriteLine("Usage: maxplayers <1-255>"); return; }
            _settings.MaxPlayers = _maxPlayers;
            SaveSettings();
            await BroadcastAllAsync(new RelayPacket { Type = "ServerLimit", MaxPlayers = _maxPlayers });
            Log("INFO", $"Max players set to {_maxPlayers}");
            return;
        }
        if (_command is "ops" or "bans" or "banlist")
        {
            IEnumerable<string> _entries;
            lock (_gate) _entries = _command == "ops" ? _accessList.Operators.Select(_entry => _entry.PlayerId).ToArray() : _accessList.Bans.Select(_entry => $"{_entry.PlayerId}: {_entry.Reason}").ToArray();
            foreach (var _entry in _entries) Console.WriteLine(_entry);
            return;
        }
        if (_parts.Length < 2 || !Guid.TryParse(_parts[1], out var _parsedId))
        {
            Console.WriteLine("A valid player UUID is required.");
            return;
        }
        var _targetId = _parsedId.ToString("D");
        if (_command == "kick")
        {
            var _target = _clients.Values.FirstOrDefault(_entry => _entry.PersistentPlayerId.Equals(_targetId, StringComparison.OrdinalIgnoreCase));
            if (_target is null) { Log("WARN", $"Kick target is not connected: {_targetId}"); return; }
            var _reason = _parts.Length == 3 ? _parts[2] : "Kicked by server administrator.";
            await SendAsync(_target.EndPoint, new RelayPacket { Type = "Reject", Reason = _reason });
            await RemoveClientAsync(_target, true);
            Log("INFO", $"Player kicked from console: {_target.Name} ({_targetId}): {_reason}");
            return;
        }
        if (_command == "op")
        {
            lock (_gate)
            {
                if (!_accessList.Operators.Any(_entry => _entry.PlayerId.Equals(_targetId, StringComparison.OrdinalIgnoreCase))) _accessList.Operators.Add(new RelayOperatorEntry { PlayerId = _targetId });
                SaveAccessList();
            }
            await BroadcastPermissionStateAsync(_targetId, true);
            Log("INFO", $"Operator added: {_targetId}");
            return;
        }
        if (_command == "deop")
        {
            lock (_gate)
            {
                _accessList.Operators.RemoveAll(_entry => _entry.PlayerId.Equals(_targetId, StringComparison.OrdinalIgnoreCase));
                SaveAccessList();
            }
            await BroadcastPermissionStateAsync(_targetId, false);
            Log("INFO", $"Operator removed: {_targetId}");
            return;
        }
        if (_command == "ban")
        {
            var _reason = _parts.Length == 3 ? _parts[2] : "Banned by server administrator.";
            lock (_gate)
            {
                _accessList.Bans.RemoveAll(_entry => _entry.PlayerId.Equals(_targetId, StringComparison.OrdinalIgnoreCase));
                _accessList.Bans.Add(new RelayBanEntry { PlayerId = _targetId, Reason = _reason, CreatedUtc = DateTime.UtcNow });
                SaveAccessList();
            }
            var _client = _clients.Values.FirstOrDefault(_entry => _entry.PersistentPlayerId.Equals(_targetId, StringComparison.OrdinalIgnoreCase));
            if (_client is not null)
            {
                await SendAsync(_client.EndPoint, new RelayPacket { Type = "Reject", Reason = _reason });
                await RemoveClientAsync(_client, true);
            }
            Log("INFO", $"Player banned: {_targetId}: {_reason}");
            return;
        }
        if (_command == "unban")
        {
            lock (_gate)
            {
                _accessList.Bans.RemoveAll(_entry => _entry.PlayerId.Equals(_targetId, StringComparison.OrdinalIgnoreCase));
                SaveAccessList();
            }
            Log("INFO", $"Player unbanned: {_targetId}");
            return;
        }
        Console.WriteLine("Unknown command. Type help for available commands.");
    }

    private async Task SendServerCommandAsync(string command, string arguments)
    {
        if (_ownerClientId is not int _ownerId || !_clients.TryGetValue(_ownerId, out var _owner))
        {
            Console.WriteLine("No pseudo-host is connected.");
            return;
        }

        await SendAsync(_owner.EndPoint, new RelayPacket { Type = "ServerCommand", Command = command, CommandArgs = arguments, RequestId = Guid.NewGuid().ToString("N") });
        Log("INFO", $"Server command queued: {command} {arguments}".TrimEnd());
    }

    private async Task RemoveClientAsync(RelayClient client, bool broadcast)
    {
        if (!_clients.TryRemove(client.ClientId, out _)) return;
        Log("INFO", $"Player left: {client.Name}, id={client.SmallId}, uuid={client.PersistentPlayerId}");
        byte _previousSmallId = client.SmallId;
        var _resetSession = false;
        lock (_gate)
        {
            if (_ownerClientId == client.ClientId)
            {
                _ownerClientId = null;
                _resetSession = true;
            }
        }
        if (_resetSession)
        {
            await ResetSessionAsync(client, _previousSmallId);
            return;
        }
        if (broadcast) await BroadcastAsync(client, new RelayPacket { Type = "PlayerLeft", ClientId = client.ClientId, SmallId = _previousSmallId, OwnerClientId = _ownerClientId ?? 0, PlatformId = client.PlatformId, PersistentPlayerId = client.PersistentPlayerId });
    }

    private async Task BroadcastPermissionStateAsync(string playerId, bool isOperator)
    {
        var _client = _clients.Values.FirstOrDefault(_entry => _entry.PersistentPlayerId.Equals(playerId, StringComparison.OrdinalIgnoreCase));
        if (_client is null) return;
        await BroadcastAllAsync(new RelayPacket { Type = "PermissionState", ClientId = _client.ClientId, SmallId = _client.SmallId, PlatformId = _client.PlatformId, PersistentPlayerId = _client.PersistentPlayerId, IsOperator = isOperator });
    }

    private async Task ResetSessionAsync(RelayClient owner, byte previousSmallId)
    {
        await BroadcastAllAsync(new RelayPacket { Type = "SessionReset", ClientId = owner.ClientId, SmallId = previousSmallId, OwnerClientId = 0, SessionResetRequired = true });
        foreach (var _client in _clients.Values.ToList()) _clients.TryRemove(_client.ClientId, out _);
    }

    private async Task BroadcastAsync(RelayClient sender, RelayPacket message)
    {
        foreach (var _client in _clients.Values)
        {
            if (_client.ClientId != sender.ClientId) await SendAsync(_client.EndPoint, message);
        }
    }

    private async Task BroadcastAllAsync(RelayPacket message)
    {
        foreach (var _client in _clients.Values) await SendAsync(_client.EndPoint, message);
    }

    private async Task SendToSmallIdAsync(byte smallId, RelayPacket message)
    {
        var _client = _clients.Values.FirstOrDefault(_value => _value.SmallId == smallId);
        if (_client is not null) await SendAsync(_client.EndPoint, message);
    }

    private async Task BroadcastExceptSmallIdAsync(byte excludedSmallId, RelayPacket message)
    {
        foreach (var _client in _clients.Values)
        {
            if (_client.SmallId != excludedSmallId) await SendAsync(_client.EndPoint, message);
        }
    }

    private async Task SendAsync(IPEndPoint endpoint, RelayPacket message)
    {
        var _bytes = JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions);
        await _socket.SendAsync(_bytes, _bytes.Length, endpoint);
    }

    private string GetLogPath() => Path.Combine(_logDirectory, $"relay-{DateTime.UtcNow:yyyy-MM-dd}.log");

    private void Log(string level, string message)
    {
        var _line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC] [{level}] {message}";
        Console.WriteLine(_line);
        try { File.AppendAllText(GetLogPath(), _line + Environment.NewLine, Encoding.UTF8); }
        catch (Exception _exception) { Console.Error.WriteLine($"Could not write relay log: {_exception.Message}"); }
    }

    private RelayPacket CreateStatePacket(string type, RelayClient client)
    {
        return new RelayPacket
        {
            Type = type,
            ClientId = client.ClientId,
            SmallId = client.SmallId,
            OwnerClientId = _ownerClientId ?? 0,
            ServerVersion = _serverVersion,
            ProtocolVersion = _protocolVersion,
            ServerId = _serverId,
            AnticheatEnabled = _settings.AnticheatEnabled,
            LevelBarcode = _levelBarcode,
            LoadingScreenBarcode = _loadingScreenBarcode,
            IsLoading = _isLoading,
            PlatformId = client.PlatformId,
            PersistentPlayerId = client.PersistentPlayerId,
            IsOperator = IsOperator(client.PersistentPlayerId)
        };
    }

    private RelayPacket CreateQueryPacket()
    {
        return new RelayPacket
        {
            Type = "ServerInfo",
            ServerId = _serverId,
            ServerVersion = _serverVersion,
            ProtocolVersion = _protocolVersion,
            LevelBarcode = _levelBarcode,
            LoadingScreenBarcode = _loadingScreenBarcode,
            IsLoading = _isLoading,
            PlayerCount = _clients.Count,
            MaxPlayers = _settings.MaxPlayers,
            ServerName = _settings.ServerName,
            HostName = _settings.HostName,
            GamemodeTitle = _settings.GamemodeTitle,
            GamemodeBarcode = _settings.GamemodeBarcode
        };
    }

    private RelayState LoadState()
    {
        try
        {
            if (!File.Exists(_stateFileName)) return new RelayState();
            return JsonSerializer.Deserialize<RelayState>(File.ReadAllBytes(_stateFileName), _jsonOptions) ?? new RelayState();
        }
        catch
        {
            return new RelayState();
        }
    }

    private RelaySettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsFileName))
            {
                var _defaultSettings = new RelaySettings();
                File.WriteAllBytes(_settingsFileName, JsonSerializer.SerializeToUtf8Bytes(_defaultSettings, _jsonOptions));
                return _defaultSettings;
            }

            return JsonSerializer.Deserialize<RelaySettings>(File.ReadAllBytes(_settingsFileName), _jsonOptions) ?? new RelaySettings();
        }
        catch
        {
            return new RelaySettings();
        }
    }

    private RelayAccessList LoadAccessList()
    {
        try
        {
            if (!File.Exists(_accessFileName))
            {
                var _defaultAccessList = new RelayAccessList();
                SaveAccessList(_defaultAccessList);
                return _defaultAccessList;
            }
            var _accessList = JsonSerializer.Deserialize<RelayAccessList>(File.ReadAllBytes(_accessFileName), _jsonOptions) ?? new RelayAccessList();
            _accessList.Operators ??= new List<RelayOperatorEntry>();
            _accessList.Bans ??= new List<RelayBanEntry>();
            return _accessList;
        }
        catch
        {
            Console.WriteLine($"Failed to read {_accessFileName}; starting with an empty access list.");
            return new RelayAccessList();
        }
    }

    private void SaveAccessList() => SaveAccessList(_accessList);

    private void SaveAccessList(RelayAccessList accessList)
    {
        var _tempPath = $"{_accessFileName}.tmp";
        try
        {
            File.WriteAllBytes(_tempPath, JsonSerializer.SerializeToUtf8Bytes(accessList, _jsonOptions));
            File.Move(_tempPath, _accessFileName, true);
        }
        catch (Exception _exception)
        {
            try { if (File.Exists(_tempPath)) File.Delete(_tempPath); } catch { }
            Console.WriteLine($"Failed to save {_accessFileName}: {_exception.Message}");
        }
    }

    private void SaveState()
    {
        var _state = new RelayState
        {
            ServerId = _serverId,
            LevelBarcode = _levelBarcode,
            LoadingScreenBarcode = _loadingScreenBarcode,
            SettingsJson = _settingsJson,
            Motd = _motd
        };
        File.WriteAllBytes(_stateFileName, JsonSerializer.SerializeToUtf8Bytes(_state, _jsonOptions));
    }

    private void SaveSettings()
    {
        File.WriteAllBytes(_settingsFileName, JsonSerializer.SerializeToUtf8Bytes(_settings, _jsonOptions));
    }
}

internal sealed class RelayClient
{
    public RelayClient(int clientId, byte smallId, IPEndPoint endPoint, string name, ulong platformId, string persistentPlayerId)
    {
        ClientId = clientId;
        SmallId = smallId;
        EndPoint = endPoint;
        Name = name;
        PlatformId = platformId;
        PersistentPlayerId = persistentPlayerId;
        LastSeenUtc = DateTime.UtcNow;
    }

    public int ClientId { get; }
    public byte SmallId { get; set; }
    public IPEndPoint EndPoint { get; }
    public string Name { get; }
    public ulong PlatformId { get; }
    public string PersistentPlayerId { get; }
    public DateTime LastSeenUtc { get; set; }
}

internal sealed class RelayPacket
{
    public string Type { get; set; } = string.Empty;
    public int ClientId { get; set; }
    public byte SmallId { get; set; }
    public byte PreviousSmallId { get; set; }
    public int OwnerClientId { get; set; }
    public int TargetClientId { get; set; }
    public int ExcludeClientId { get; set; }
    public byte TargetSmallId { get; set; }
    public byte ExcludeSmallId { get; set; }
    public int ProtocolVersion { get; set; }
    public bool OwnerChanged { get; set; }
    public bool SessionResetRequired { get; set; }
    public bool IsServerHandled { get; set; }
    public int Channel { get; set; }
    public ulong PlatformId { get; set; }
    public ulong TargetPlatformId { get; set; }
    public string? ServerId { get; set; }
    public string? LevelBarcode { get; set; }
    public string? LoadingScreenBarcode { get; set; }
    public bool IsLoading { get; set; }
    public bool AnticheatEnabled { get; set; }
    public bool IsOperator { get; set; }
    public int PlayerCount { get; set; }
    public int MaxPlayers { get; set; }
    public string? ServerName { get; set; }
    public string? HostName { get; set; }
    public string? GamemodeTitle { get; set; }
    public string? GamemodeBarcode { get; set; }
    public string? ServerVersion { get; set; }
    public string? Name { get; set; }
    public string? Reason { get; set; }
    public string? PersistentPlayerId { get; set; }
    public string? Mode { get; set; }
    public string? Payload { get; set; }
    public string? SettingsJson { get; set; }
    public string? Command { get; set; }
    public string? CommandArgs { get; set; }
    public string? RequestId { get; set; }
}

internal sealed class RelayState
{
    public string? ServerId { get; set; }
    public string? LevelBarcode { get; set; }
    public string? LoadingScreenBarcode { get; set; }
    public string? SettingsJson { get; set; }
    public string? Motd { get; set; }
}

internal sealed class RelaySettings
{
    public bool AnticheatEnabled { get; set; }
    public int MaxPlayers { get; set; } = 255;
    public string? ServerName { get; set; }
    public string? HostName { get; set; }
    public string? GamemodeTitle { get; set; }
    public string? GamemodeBarcode { get; set; }
}

internal sealed class RelayAccessList
{
    public List<RelayOperatorEntry> Operators { get; set; } = new();
    public List<RelayBanEntry> Bans { get; set; } = new();
}

internal sealed class RelayOperatorEntry
{
    public string PlayerId { get; set; } = string.Empty;
}

internal sealed class RelayBanEntry
{
    public string PlayerId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}
