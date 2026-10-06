namespace LabFusion.Network;

public class SteamVRNetworkLayer : DedicatedServerNetworkLayer
{
    public const int SteamVRId = 250820;

    public override uint ApplicationID => SteamVRId;

    public override string Title => "Dedicated Server";
}
