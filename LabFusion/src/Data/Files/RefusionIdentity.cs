using System.Text.Json;
using LabFusion.Utilities;

namespace LabFusion.Data;

public static class RefusionIdentity
{
    private const string _fileName = "refusion-identity.json";
    private static string _playerId = string.Empty;

    public static string PlayerId => _playerId;

    public static void OnInitialize()
    {
        var _path = PersistentData.GetPath(_fileName);
        if (File.Exists(_path))
        {
            RefusionIdentityData _identity = null;
            try
            {
                _identity = JsonSerializer.Deserialize<RefusionIdentityData>(File.ReadAllText(_path));
            }
            catch (JsonException)
            {
            }
            if (_identity != null && Guid.TryParse(_identity.PlayerId, out var _parsedId))
            {
                _playerId = _parsedId.ToString("D");
                return;
            }
        }

        _playerId = Guid.NewGuid().ToString("D");
        var _tempPath = $"{_path}.tmp";
        try
        {
            File.WriteAllText(_tempPath, JsonSerializer.Serialize(new RefusionIdentityData() { PlayerId = _playerId }));
            File.Move(_tempPath, _path, true);
        }
        catch (Exception _exception)
        {
            try { if (File.Exists(_tempPath)) File.Delete(_tempPath); } catch { }
            FusionLogger.Warn($"Could not persist the Refusion player identity: {_exception.Message}");
        }
    }
}

public sealed class RefusionIdentityData
{
    public string PlayerId { get; set; } = string.Empty;
}
