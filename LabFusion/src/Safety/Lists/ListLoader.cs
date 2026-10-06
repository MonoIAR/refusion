namespace LabFusion.Safety;

public static class ListLoader
{
    internal static void OnInitializeMelon()
    {
        ProfanityListManager.FetchFile();
        GlobalModBlacklistManager.FetchFile();
        URLWhitelistManager.FetchFile();
    }
}
