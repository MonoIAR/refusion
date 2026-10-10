using Il2CppSLZ.Marrow.Warehouse;

using LabFusion.Downloading.ModIO;
using LabFusion.Extensions;
using LabFusion.Marrow;
using LabFusion.Network.Serialization;
using LabFusion.Player;
using LabFusion.Utilities;

namespace LabFusion.Network;

public class ModInfoRequestData : INetSerializable
{
    public int? GetSize() => Barcode.GetSize() + sizeof(uint);

    public string Barcode;

    public uint TrackerID;

    public void Serialize(INetSerializer serializer)
    {
        serializer.SerializeValue(ref Barcode);
        serializer.SerializeValue(ref TrackerID);
    }

    public static ModInfoRequestData Create(string barcode, uint trackerID)
    {
        return new ModInfoRequestData()
        {
            Barcode = barcode,
            TrackerID = trackerID,
        };
    }
}

public class ModInfoRequestMessage : NativeMessageHandler
{
    public override byte Tag => NativeMessageTag.ModInfoRequest;

    protected override void OnHandleMessage(ReceivedMessage received)
    {
        // Read request
        var data = received.ReadData<ModInfoRequestData>();

        // Make sure we're the target
        if (received.Route.Target != PlayerIDManager.LocalSmallID)
        {
            throw new Exception($"Received a ModInfoRequest, but we were not the desired target of {received.Route.Target.Value}!");
        }

        // Get the crate from the barcode
        var crate = CrateFilterer.GetCrate<Crate>(new(data.Barcode));

        if (crate == null)
        {
#if DEBUG
            FusionLogger.Warn($"Mod info request for {data.Barcode} was dropped: the crate was not found.");
#endif

            return;
        }

        var pallet = crate.Pallet;

        // Make sure the pallet isn't part of a marrow game and is a mod
        if (pallet.IsInMarrowGame())
        {
#if DEBUG
            FusionLogger.Warn($"Mod info request for {data.Barcode} was dropped: the pallet is part of a marrow game.");
#endif

            return;
        }

        // Get the mod info
        var manifest = CrateFilterer.GetManifest(pallet);

        if (manifest == null)
        {
#if DEBUG
            FusionLogger.Warn($"Mod info request for {data.Barcode} was dropped: the manifest was not found.");
#endif

            return;
        }

        var modListing = manifest.ModListing;
        
        var modTarget = ModIOManager.GetTargetFromListing(modListing);

        if (modTarget == null)
        {
#if DEBUG
            FusionLogger.Warn($"Mod info request for {data.Barcode} was dropped: the listing has no mod.io target.");
#endif

            return;
        }

        // Write and send response
        var modFile = new SerializedModIOFile(new ModIOFile((int)modTarget.ModId, (int)modTarget.ModfileId));

        var writtenData = ModInfoResponseData.Create(modFile, data.TrackerID);

        MessageRelay.RelayNative(writtenData, NativeMessageTag.ModInfoResponse, new MessageRoute(received.Sender.Value, NetworkChannel.Reliable));
    }
}