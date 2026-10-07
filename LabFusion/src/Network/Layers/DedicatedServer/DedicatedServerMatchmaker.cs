using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using LabFusion.Data;
using LabFusion.Utilities;

using MelonLoader;

namespace LabFusion.Network;

public sealed class DedicatedServerMatchmaker : IMatchmaker
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public void RequestLobbies(Action<IMatchmaker.MatchmakerCallbackInfo> callback) => RequestLobbies(MatchmakerFilters.Empty, callback);

    public void RequestLobbies(MatchmakerFilters filters, Action<IMatchmaker.MatchmakerCallbackInfo> callback) => MelonCoroutines.Start(QueryHistory(callback));

    public void RequestLobbiesByCode(string code, Action<IMatchmaker.MatchmakerCallbackInfo> callback) => MelonCoroutines.Start(QueryAddress(code, callback));

    private static IEnumerator QueryHistory(Action<IMatchmaker.MatchmakerCallbackInfo> callback)
    {
        var _lobbies = new List<IMatchmaker.LobbyInfo>();
        foreach (var _address in DedicatedServerHistory.Addresses.ToArray())
        {
            var _info = Query(_address);
            if (_info != null) _lobbies.Add(CreateLobby(_address, _info));
            yield return null;
        }
        callback?.Invoke(new IMatchmaker.MatchmakerCallbackInfo { Lobbies = _lobbies.ToArray() });
    }

    private static IEnumerator QueryAddress(string address, Action<IMatchmaker.MatchmakerCallbackInfo> callback)
    {
        var _info = Query(address);
        callback?.Invoke(_info == null ? IMatchmaker.MatchmakerCallbackInfo.Empty : new IMatchmaker.MatchmakerCallbackInfo { Lobbies = new[] { CreateLobby(address, _info) } });
        yield break;
    }

    private static DedicatedServerInfo? Query(string address)
    {
        if (!TryParseAddress(address, out var _endpoint)) return null;
        using var _udp = new UdpClient();
        _udp.Client.ReceiveTimeout = 1500;
        var _request = JsonSerializer.SerializeToUtf8Bytes(new RelayPacket { Type = "Query" }, _jsonOptions);
        try
        {
            _udp.Send(_request, _request.Length, _endpoint);
            var _remote = new IPEndPoint(IPAddress.Any, 0);
            var _response = _udp.Receive(ref _remote);
            return JsonSerializer.Deserialize<DedicatedServerInfo>(Encoding.UTF8.GetString(_response), _jsonOptions);
        }
        catch (Exception _exception)
        {
            FusionLogger.Warn($"Could not query dedicated server {address}: {_exception.Message}");
            return null;
        }
    }

    private static bool TryParseAddress(string address, out IPEndPoint endpoint)
    {
        endpoint = null;
        var _parts = address.Split(':', 2, StringSplitOptions.TrimEntries);
        if (_parts.Length != 2 || string.IsNullOrWhiteSpace(_parts[0]) || !int.TryParse(_parts[1], out var _port) || _port is < 1 or > 65535) return false;
        var _hostText = _parts[0];
        if (IPAddress.TryParse(_hostText, out var _ip))
        {
            if (!string.Equals(_ip.ToString(), _hostText, StringComparison.Ordinal)) return false;
            endpoint = new IPEndPoint(_ip, _port);
            return true;
        }
        if (Uri.CheckHostName(_hostText) == UriHostNameType.Unknown || !HasLetter(_hostText)) return false;
        try
        {
            var _addresses = Dns.GetHostAddresses(_hostText);
            var _resolved = _addresses.FirstOrDefault(_entry => _entry.AddressFamily == AddressFamily.InterNetwork) ?? _addresses.FirstOrDefault();
            if (_resolved == null) return false;
            endpoint = new IPEndPoint(_resolved, _port);
            return true;
        }
        catch { return false; }
    }

    private static bool HasLetter(string text)
    {
        foreach (var _character in text)
        {
            if (char.IsLetter(_character)) return true;
        }
        return false;
    }

    private static IMatchmaker.LobbyInfo CreateLobby(string address, DedicatedServerInfo info)
    {
        var _lobby = new DedicatedServerLobby(address);
        var _lobbyInfo = new LobbyInfo
        {
            LobbyID = DedicatedServerLobby.GetLobbyId(address),
            LobbyCode = address,
            LobbyName = info.ServerName,
            LobbyHostName = info.HostName,
            LobbyVersion = Version.TryParse(info.ServerVersion, out var _version) ? _version : new Version(),
            PlayerCount = info.PlayerCount,
            MaxPlayers = info.MaxPlayers,
            LevelTitle = info.LevelBarcode,
            LevelBarcode = info.LevelBarcode,
            GamemodeTitle = info.GamemodeTitle,
            GamemodeBarcode = info.GamemodeBarcode,
            Privacy = ServerPrivacy.PUBLIC
        };
        var _metadata = new LobbyMetadataInfo
        {
            LobbyInfo = _lobbyInfo,
            HasLobbyOpen = !info.IsLoading,
            ClientHasLevel = true,
            LobbyCode = address,
            Privacy = ServerPrivacy.PUBLIC,
            Full = info.PlayerCount >= info.MaxPlayers,
            VersionMajor = _lobbyInfo.LobbyVersion.Major,
            VersionMinor = _lobbyInfo.LobbyVersion.Minor,
            Game = Support.GameInfo.GameName
        };
        return new IMatchmaker.LobbyInfo { Lobby = _lobby, Metadata = _metadata };
    }

    private sealed class RelayPacket
    {
        public string Type { get; set; } = string.Empty;
    }
}

public sealed class DedicatedServerInfo
{
    public string? Type { get; set; }
    public string? ServerId { get; set; }
    public string? ServerVersion { get; set; }
    public string? LevelBarcode { get; set; }
    public bool IsLoading { get; set; }
    public int PlayerCount { get; set; }
    public int MaxPlayers { get; set; }
    public string? ServerName { get; set; }
    public string? HostName { get; set; }
    public string? GamemodeTitle { get; set; }
    public string? GamemodeBarcode { get; set; }
}

public sealed class DedicatedServerLobby : NetworkLobby
{
    private readonly string _address;
    private readonly Dictionary<string, string> _metadata = new();

    public DedicatedServerLobby(string address) => _address = address;

    public override string GetMetadata(string key) => _metadata.TryGetValue(key, out var _value) ? _value : string.Empty;

    public override void SetMetadata(string key, string value) { _metadata[key] = value ?? string.Empty; SaveKey(key); }

    public override bool TryGetMetadata(string key, out string value) => _metadata.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value);

    public override Action CreateJoinDelegate(ulong lobbyId) => () =>
    {
        DedicatedServerHistory.Add(_address);
        if (NetworkLayerManager.Layer is DedicatedServerNetworkLayer _layer) _layer.JoinServerByAddress(_address);
    };

    public static ulong GetLobbyId(string address)
    {
        unchecked
        {
            ulong _hash = 1469598103934665603;
            foreach (var _char in address) _hash = (_hash ^ _char) * 1099511628211;
            return _hash == 0 ? 1 : _hash;
        }
    }
}
