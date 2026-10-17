using LabFusion.Network.Serialization;
using LabFusion.Utilities;

namespace LabFusion.Network;

public class ChunkedPayloadData : INetSerializable
{
    public byte InnerTag;
    public byte TransferId;
    public ushort ChunkIndex;
    public ushort ChunkCount;
    public int TotalLength;
    public byte[] Payload;

    public int? GetSize() => sizeof(byte) + sizeof(byte) + sizeof(ushort) + sizeof(ushort) + sizeof(int) + sizeof(int) + (Payload?.Length ?? 0);

    public void Serialize(INetSerializer serializer)
    {
        serializer.SerializeValue(ref InnerTag);
        serializer.SerializeValue(ref TransferId);
        serializer.SerializeValue(ref ChunkIndex);
        serializer.SerializeValue(ref ChunkCount);
        serializer.SerializeValue(ref TotalLength);
        serializer.SerializeValue(ref Payload);
    }
}

public class ChunkedPayloadMessage : NativeMessageHandler
{
    private const int MaxChunkSize = 640;
    private const int MaxChunkCount = 64;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private static readonly Dictionary<byte, ChunkBuffer> _buffers = new();

    private class ChunkBuffer
    {
        public byte TransferId;
        public ushort ChunkCount;
        public int TotalLength;
        public DateTime LastActivityUtc;
        public Dictionary<ushort, byte[]> Chunks = new();
    }

    public override byte Tag => NativeMessageTag.ChunkedPayload;

    public override ExpectedReceiverType ExpectedReceiver => ExpectedReceiverType.ClientsOnly;

    protected override void OnHandleMessage(ReceivedMessage received)
    {
        var data = received.ReadData<ChunkedPayloadData>();

        if (!IsChunkValid(data))
        {
            return;
        }

        PurgeStaleBuffers();

        // Any first chunk may start a buffer (chunk 0 itself can be lost); a chunk 0 of a new transfer supersedes a stale one
        if (!_buffers.TryGetValue(data.InnerTag, out var buffer) || buffer.TransferId != data.TransferId)
        {
            if (buffer != null && data.ChunkIndex != 0)
            {
                return;
            }

            buffer = new ChunkBuffer()
            {
                TransferId = data.TransferId,
                ChunkCount = data.ChunkCount,
                TotalLength = data.TotalLength,
                LastActivityUtc = DateTime.UtcNow,
            };

            _buffers[data.InnerTag] = buffer;
        }

        buffer.Chunks[data.ChunkIndex] = data.Payload;
        buffer.LastActivityUtc = DateTime.UtcNow;

        if (buffer.Chunks.Count < buffer.ChunkCount)
        {
            return;
        }

        int receivedLength = 0;
        foreach (var chunk in buffer.Chunks.Values)
        {
            receivedLength += chunk.Length;
        }

        if (receivedLength != buffer.TotalLength)
        {
            FusionLogger.Warn($"Discarded chunked payload of tag {data.InnerTag}: chunk lengths ({receivedLength}) didn't match the total ({buffer.TotalLength}).");
            _buffers.Remove(data.InnerTag);
            return;
        }

        _buffers.Remove(data.InnerTag);

        var bytes = new byte[buffer.TotalLength];
        int offset = 0;
        for (ushort i = 0; i < buffer.ChunkCount; i++)
        {
            var chunk = buffer.Chunks[i];
            Array.Copy(chunk, 0, bytes, offset, chunk.Length);
            offset += chunk.Length;
        }

        var payload = new ReceivedMessage()
        {
            Route = CommonMessageRoutes.None,
            Sender = null,
            PlatformID = null,
            Bytes = bytes,
            IsServerHandled = false,
        };

        var handler = NativeMessageHandler.Handlers[data.InnerTag];

        handler?.StartHandlingMessage(payload);
    }

    private static bool IsChunkValid(ChunkedPayloadData data)
    {
        if (data.Payload == null || data.Payload.Length == 0 || data.Payload.Length > MaxChunkSize)
        {
            return false;
        }

        if (data.ChunkCount < 1 || data.ChunkCount > MaxChunkCount || data.ChunkIndex >= data.ChunkCount)
        {
            return false;
        }

        if (data.TotalLength < 0 || data.TotalLength > MaxChunkCount * MaxChunkSize)
        {
            return false;
        }

        // Only known inner messages may be reassembled
        return data.InnerTag == NativeMessageTag.DynamicsAssignment;
    }

    private static void PurgeStaleBuffers()
    {
        List<byte> stale = null;

        foreach (var pair in _buffers)
        {
            if (DateTime.UtcNow - pair.Value.LastActivityUtc > _timeout)
            {
                (stale ??= new List<byte>()).Add(pair.Key);
            }
        }

        if (stale == null)
        {
            return;
        }

        foreach (var tag in stale)
        {
            _buffers.Remove(tag);
            FusionLogger.Warn($"Discarded an incomplete chunked payload of tag {tag} after {_timeout.TotalSeconds} seconds.");
        }
    }
}
