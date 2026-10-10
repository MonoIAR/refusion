using LabFusion.Network.Serialization;
using LabFusion.Scene;
using LabFusion.Utilities;

namespace LabFusion.Network;

public class LevelLoadData : INetSerializable
{
    public string LevelBarcode;

    public string LoadingScreenBarcode;

    public int? GetSize() => LevelBarcode.GetSize() + LoadingScreenBarcode.GetSize();

    public void Serialize(INetSerializer serializer)
    {
        serializer.SerializeValue(ref LevelBarcode);
        serializer.SerializeValue(ref LoadingScreenBarcode);
    }

    public static LevelLoadData Create(string levelBarcode, string loadBarcode)
    {
        return new LevelLoadData()
        {
            LevelBarcode = levelBarcode,
            LoadingScreenBarcode = loadBarcode
        };
    }
}

public class LevelLoadMessage : NativeMessageHandler
{
    private const float _duplicateWindow = 30f;
    private static string _lastProcessedBarcode = string.Empty;
    private static float _lastProcessedTime = float.NegativeInfinity;

    public override byte Tag => NativeMessageTag.SceneLoad;

    public override ExpectedReceiverType ExpectedReceiver => ExpectedReceiverType.ClientsOnly;

    protected override void OnHandleMessage(ReceivedMessage received)
    {
        var data = received.ReadData<LevelLoadData>();

        // Re-sent joins can deliver the same target scene again, which would restart an in-progress load
        if (data.LevelBarcode == _lastProcessedBarcode && TimeReferences.TimeSinceStartup - _lastProcessedTime < _duplicateWindow)
        {
            return;
        }

        _lastProcessedBarcode = data.LevelBarcode;
        _lastProcessedTime = TimeReferences.TimeSinceStartup;

#if DEBUG
        FusionLogger.Log($"Received level load for {data.LevelBarcode}!");
#endif

        FusionSceneManager.SetTargetScene(data.LevelBarcode, data.LoadingScreenBarcode);
    }
}