using LabFusion.Utilities;

namespace LabFusion.Network;

public static class RelayMessageDispatcher
{
    public static void Dispatch(byte[] messageBytes, bool isServerHandled = false, ulong? platformID = null)
    {
        if (messageBytes == null || messageBytes.Length == 0) return;
        try
        {
            unsafe
            {
                fixed (byte* _messagePtr = messageBytes)
                {
                    var _messageSpan = new ReadOnlySpan<byte>(_messagePtr, messageBytes.Length);

                    var _readableMessage = new ReadableMessage()
                    {
                        Buffer = _messageSpan,
                        IsServerHandled = isServerHandled,
                        PlatformID = platformID,
                    };

                    NativeMessageHandler.ReadMessage(_readableMessage);
                }
            }
        }
        catch (Exception _exception)
        {
            FusionLogger.Error($"Failed reading message from the relay server with reason: {_exception.Message}\nTrace:{_exception.StackTrace}");
        }
    }
}
