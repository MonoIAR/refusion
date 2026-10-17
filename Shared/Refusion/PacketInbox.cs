using System;
using System.Collections.Generic;
using System.Net;

namespace Refusion.Transport;

public sealed class InboundPacket
{
    public IPEndPoint Endpoint { get; init; }
    public RelayPacket Packet { get; init; }
    public bool Unconnected { get; init; }
    public bool Local { get; init; }
    public int Size { get; init; }
    public DateTime Time { get; init; } = DateTime.UtcNow;
}

public sealed class PacketInbox
{
    private readonly object _gate = new();
    private readonly Queue<InboundPacket> _reliable = new();
    private readonly Queue<InboundPacket> _realtime = new();
    private readonly Dictionary<string, InboundPacket> _poses = new();
    private readonly Dictionary<string, uint> _sequences = new();
    private readonly Dictionary<string, int> _reliableBytes = new();
    private int _realtimeBytes;
    public long Coalesced { get; private set; }
    public long Expired { get; private set; }

    public bool SetEnqueue(InboundPacket item)
    {
        lock (_gate)
        {
            var _endpoint = item.Endpoint.ToString();
            if (item.Packet.Payload == null || item.Packet.Channel == 0)
            {
                _reliableBytes.TryGetValue(_endpoint, out var _bytes);
                if (_bytes + item.Size > WireProtocol.MaxReliableBytes || _reliable.Count > 32768) return false;
                _reliableBytes[_endpoint] = _bytes + item.Size;
                _reliable.Enqueue(item);
                return true;
            }
            var _key = FusionEnvelope.GetStateKey(item.Packet);
            if (_key != null)
            {
                if (_sequences.TryGetValue(_key, out var _sequence) && !FusionEnvelope.GetIsNewer(item.Packet.Sequence, _sequence)) { Expired++; return true; }
                _sequences[_key] = item.Packet.Sequence;
                if (_poses.TryGetValue(_key, out var _previous)) { _realtimeBytes -= _previous.Size; Coalesced++; }
                else if (_poses.Count >= 4096) { Expired++; return true; }
                _poses[_key] = item;
            }
            else
            {
                _realtime.Enqueue(item);
            }
            _realtimeBytes += item.Size;
            while (_realtime.Count > 64 || _realtimeBytes > 2 * 1024 * 1024)
            {
                if (_realtime.Count > 0) _realtimeBytes -= _realtime.Dequeue().Size;
                else { foreach (var _entry in _poses) { _realtimeBytes -= _entry.Value.Size; _poses.Remove(_entry.Key); break; } }
                Expired++;
            }
            if (_sequences.Count > 8192) _sequences.Clear();
            return true;
        }
    }

    public bool GetTryDequeue(out InboundPacket item)
    {
        lock (_gate)
        {
            if (_reliable.Count > 0)
            {
                item = _reliable.Dequeue();
                _reliableBytes[item.Endpoint.ToString()] -= item.Size;
                return true;
            }
            while (_realtime.Count > 0)
            {
                item = _realtime.Dequeue();
                _realtimeBytes -= item.Size;
                if (DateTime.UtcNow - item.Time <= TimeSpan.FromMilliseconds(250)) return true;
                Expired++;
            }
            foreach (var _entry in _poses)
            {
                item = _entry.Value;
                _poses.Remove(_entry.Key);
                _realtimeBytes -= item.Size;
                return true;
            }
            item = null;
            return false;
        }
    }

    public void SetClear()
    {
        lock (_gate) { _reliable.Clear(); _realtime.Clear(); _poses.Clear(); _sequences.Clear(); _reliableBytes.Clear(); _realtimeBytes = 0; }
    }
}
