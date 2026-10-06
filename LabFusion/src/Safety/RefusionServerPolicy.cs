namespace LabFusion.Safety;

public static class RefusionServerPolicy
{
    public static bool AnticheatEnabled { get; private set; } = false;
    public static bool LegacyHostModBlacklistEnabled { get; private set; } = false;

    public static void SetAnticheatEnabled(bool enabled) => AnticheatEnabled = enabled;
    public static void SetLegacyHostModBlacklistEnabled(bool enabled) => LegacyHostModBlacklistEnabled = enabled;
}
