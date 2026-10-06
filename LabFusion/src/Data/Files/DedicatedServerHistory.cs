using System.Text.Json;
using LabFusion.Utilities;

namespace LabFusion.Data;

public static class DedicatedServerHistory
{
    private const string _fileName = "dedicated-server-history.json";
    private const int _maxEntries = 32;
    private static readonly List<string> _addresses = new();
    private static bool _loaded;

    public static IReadOnlyList<string> Addresses
    {
        get
        {
            EnsureLoaded();
            return _addresses;
        }
    }

    public static void Add(string address)
    {
        EnsureLoaded();
        address = address.Trim();
        if (string.IsNullOrWhiteSpace(address)) return;
        _addresses.RemoveAll(_value => string.Equals(_value, address, StringComparison.OrdinalIgnoreCase));
        _addresses.Insert(0, address);
        if (_addresses.Count > _maxEntries) _addresses.RemoveRange(_maxEntries, _addresses.Count - _maxEntries);
        Save();
    }

    public static void Remove(string address)
    {
        EnsureLoaded();
        _addresses.RemoveAll(_value => string.Equals(_value, address, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var _path = PersistentData.GetPath(_fileName);
            if (!File.Exists(_path)) return;
            var _data = JsonSerializer.Deserialize<DedicatedServerHistoryData>(File.ReadAllText(_path));
            if (_data?.Addresses == null) return;
            foreach (var _address in _data.Addresses)
            {
                if (!string.IsNullOrWhiteSpace(_address) && !_addresses.Contains(_address, StringComparer.OrdinalIgnoreCase)) _addresses.Add(_address.Trim());
            }
        }
        catch (Exception _exception)
        {
            FusionLogger.Warn($"Could not load dedicated server history: {_exception.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            var _path = PersistentData.GetPath(_fileName);
            var _tempPath = $"{_path}.tmp";
            File.WriteAllText(_tempPath, JsonSerializer.Serialize(new DedicatedServerHistoryData { Addresses = _addresses }));
            File.Move(_tempPath, _path, true);
        }
        catch (Exception _exception)
        {
            FusionLogger.Warn($"Could not save dedicated server history: {_exception.Message}");
        }
    }
}

public sealed class DedicatedServerHistoryData
{
    public List<string> Addresses { get; set; } = new();
}
