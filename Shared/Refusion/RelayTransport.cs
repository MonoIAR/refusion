using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;

namespace Refusion.Transport;

public sealed class RelayTransport : IDisposable
{
    private sealed class Pending
    {
        public byte[] Bytes;
        public byte Lane;
        public DateTime Time = DateTime.UtcNow;
    }
    private sealed class PeerState
    {
        public NetPeer Peer;
        public readonly object Gate = new();
        public readonly Queue<Pending> Reliable = new();
        public readonly Queue<Pending> Realtime = new();
        public readonly Dictionary<string, Pending> Poses = new();
        public readonly Dictionary<string, uint> Sequences = new();
        public readonly RealtimeFragments Fragments = new();
        public long ReliableBytes;
        public int RealtimeBytes;
        public int BytesPerSecond = 1024 * 1024;
        public double Tokens = 2 * 1024 * 1024;
        public DateTime LastRefill = DateTime.UtcNow;
        public DateTime LastProgress = DateTime.UtcNow;
        public uint FrameId;
    }
    private readonly NetManager _manager;
    private readonly ConcurrentDictionary<string, PeerState> _peers = new();
    private readonly PacketInbox _inbox = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sender;
    private readonly bool _server;
    public int Port => _manager.LocalPort;
    public long Coalesced => _inbox.Coalesced + Interlocked.Read(ref _coalesced);
    public long Expired => _inbox.Expired + Interlocked.Read(ref _expired);
    public long Rejected => Interlocked.Read(ref _rejected);
    private long _coalesced;
    private long _expired;
    private long _rejected;

    public RelayTransport(int port = 0, bool server = false)
    {
        _server = server;
        var _listener = new EventBasedNetListener();
        _manager = new NetManager(_listener) { AutoRecycle = true, UnsyncedEvents = true, UnsyncedReceiveEvent = true, UnsyncedDeliveryEvent = true, ChannelsCount = 2, MtuOverride = WireProtocol.Mtu, MtuDiscovery = false, DisconnectTimeout = 15000, ReconnectDelay = 1000, MaxConnectAttempts = 10, EnableStatistics = true, UnconnectedMessagesEnabled = true };
        _listener.ConnectionRequestEvent += _request => { if (_server && _manager.ConnectedPeersCount < 255) _request.AcceptIfKey(WireProtocol.Key); else _request.Reject(); };
        _listener.PeerConnectedEvent += _peer => _peers.GetOrAdd(_peer.ToString(), _ => new PeerState { Peer = _peer });
        _listener.PeerDisconnectedEvent += (_peer, _info) =>
        {
            if (_peers.TryGetValue(_peer.ToString(), out var _state) && ReferenceEquals(_state.Peer, _peer)) _peers.TryRemove(_peer.ToString(), out _);
            var _reason = _info.Reason.ToString();
            try { if (_info.AdditionalData.AvailableBytes > 0) _reason = System.Text.Encoding.UTF8.GetString(_info.AdditionalData.GetRemainingBytes()); } catch { }
            _inbox.SetEnqueue(new InboundPacket { Endpoint = _peer, Size = 256, Local = true, Packet = new RelayPacket { Type = "TransportClosed", Reason = _reason } });
        };
        _listener.NetworkReceiveEvent += (_peer, _reader, _channel, _delivery) =>
        {
            try
            {
                if (!_peers.TryGetValue(_peer.ToString(), out var _state) || !ReferenceEquals(_state.Peer, _peer)) return;
                var _bytes = _reader.GetRemainingBytes();
                if (_delivery == DeliveryMethod.Unreliable) { lock (_state.Gate) _bytes = _state.Fragments.GetAccept(_bytes); if (_bytes == null) return; }
                var _packet = WireProtocol.GetDecode(_bytes);
                var _expected = _packet.Payload == null || _packet.Channel == 0 ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;
                if (_delivery != _expected || (_delivery == DeliveryMethod.ReliableOrdered && _channel != (_packet.Payload == null ? 0 : 1))) throw new InvalidDataException("delivery_channel_mismatch");
                if (!_inbox.SetEnqueue(new InboundPacket { Endpoint = _peer, Size = _bytes.Length, Packet = _packet })) SetDisconnect(_peer, "reliable_receive_backlog_overflow");
            }
            catch (Exception _exception) { Interlocked.Increment(ref _rejected); SetDisconnect(_peer, _exception.Message); }
        };
        _listener.NetworkReceiveUnconnectedEvent += (_endpoint, _reader, _) =>
        {
            try
            {
                if (_reader.AvailableBytes > WireProtocol.Mtu) return;
                var _bytes = _reader.GetRemainingBytes();
                var _packet = WireProtocol.GetDecode(_bytes);
                if ((_server && _packet.Type != "Query") || (!_server && _packet.Type != "ServerInfo")) return;
                _inbox.SetEnqueue(new InboundPacket { Endpoint = _endpoint, Packet = _packet, Size = _bytes.Length, Unconnected = true });
            }
            catch { Interlocked.Increment(ref _rejected); }
        };
        _listener.DeliveryEvent += (_peer, _data) =>
        {
            if (_data is int _size && _peers.TryGetValue(_peer.ToString(), out var _state)) lock (_state.Gate) { _state.ReliableBytes -= _size; _state.LastProgress = DateTime.UtcNow; }
        };
        if (!_manager.Start(port)) throw new IOException("Failed to start relay transport.");
        _sender = Task.Run(GetSendLoopAsync);
    }

    public void SetConnect(IPEndPoint endpoint)
    {
        var _peer = _manager.Connect(endpoint, WireProtocol.Key);
        _peers.GetOrAdd(endpoint.ToString(), _ => new PeerState { Peer = _peer });
    }

    public void SetBudget(IPEndPoint endpoint, int bytesPerSecond)
    {
        if (_peers.TryGetValue(endpoint.ToString(), out var _state)) lock (_state.Gate) _state.BytesPerSecond = Math.Clamp(bytesPerSecond, 65536, 64 * 1024 * 1024);
    }

    public void SetSend(IPEndPoint endpoint, RelayPacket packet)
    {
        if (!_peers.TryGetValue(endpoint.ToString(), out var _state))
        {
            if (packet.Type is "Query" or "ServerInfo") SetUnconnected(endpoint, packet);
            return;
        }
        lock (_state.Gate)
        {
            var _key = FusionEnvelope.GetStateKey(packet);
            if (_key != null && packet.Sequence == 0)
            {
                _state.Sequences.TryGetValue(_key, out var _sequence);
                packet.Sequence = _sequence + 1;
                _state.Sequences[_key] = packet.Sequence;
            }
            var _bytes = WireProtocol.GetEncode(packet);
            var _pending = new Pending { Bytes = _bytes, Lane = packet.Payload == null ? (byte)0 : (byte)1 };
            if (packet.Payload == null || packet.Channel == 0)
            {
                if (_state.ReliableBytes + _bytes.Length > WireProtocol.MaxReliableBytes) { SetDisconnect(endpoint, "reliable_send_backlog_overflow"); return; }
                if (_state.ReliableBytes == 0) _state.LastProgress = DateTime.UtcNow;
                _state.ReliableBytes += _bytes.Length;
                _state.Reliable.Enqueue(_pending);
            }
            else
            {
                if (_key != null)
                {
                    if (_state.Poses.TryGetValue(_key, out var _old)) { _state.RealtimeBytes -= _old.Bytes.Length; Interlocked.Increment(ref _coalesced); }
                    else if (_state.Poses.Count >= 4096) { Interlocked.Increment(ref _expired); return; }
                    _state.Poses[_key] = _pending;
                }
                else _state.Realtime.Enqueue(_pending);
                _state.RealtimeBytes += _bytes.Length;
                while (_state.Realtime.Count > 64 || _state.RealtimeBytes > 2 * 1024 * 1024)
                {
                    if (_state.Realtime.Count > 0) _state.RealtimeBytes -= _state.Realtime.Dequeue().Bytes.Length;
                    else { foreach (var _entry in _state.Poses) { _state.RealtimeBytes -= _entry.Value.Bytes.Length; _state.Poses.Remove(_entry.Key); break; } }
                    Interlocked.Increment(ref _expired);
                }
            }
        }
    }

    public void SetUnconnected(IPEndPoint endpoint, RelayPacket packet)
    {
        var _bytes = WireProtocol.GetEncode(packet);
        if (_bytes.Length <= WireProtocol.Mtu - 1) _manager.SendUnconnectedMessage(_bytes, endpoint);
    }

    public bool GetTryReceive(out InboundPacket packet) => _inbox.GetTryDequeue(out packet);
    public long GetReliableBytes(IPEndPoint endpoint)
    {
        return _peers.TryGetValue(endpoint.ToString(), out var _state) ? Interlocked.Read(ref _state.ReliableBytes) : 0;
    }
    public bool GetIsAlive(IPEndPoint endpoint) => _peers.TryGetValue(endpoint.ToString(), out var _state) && _state.Peer.ConnectionState == ConnectionState.Connected;

    public long GetPendingBytes()
    {
        long _total = 0;
        foreach (var _state in _peers.Values) _total += Interlocked.Read(ref _state.ReliableBytes);
        return _total;
    }
    public long GetPacketLoss()
    {
        long _total = 0;
        foreach (var _state in _peers.Values) _total += _state.Peer.Statistics.PacketLoss;
        return _total;
    }

    public void SetDisconnect(IPEndPoint endpoint, string reason)
    {
        if (_peers.TryGetValue(endpoint.ToString(), out var _state)) _manager.DisconnectPeer(_state.Peer, System.Text.Encoding.UTF8.GetBytes(reason));
    }

    private async Task GetSendLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                foreach (var _state in _peers.Values)
                {
                    lock (_state.Gate)
                    {
                        _state.Fragments.SetExpire();
                        if (_state.Peer.ConnectionState != ConnectionState.Connected) continue;
                        var _now = DateTime.UtcNow;
                        _state.Tokens = Math.Min(2.0 * _state.BytesPerSecond, _state.Tokens + (_now - _state.LastRefill).TotalSeconds * _state.BytesPerSecond);
                        _state.LastRefill = _now;
                        if (_state.ReliableBytes > 0 && _now - _state.LastProgress > TimeSpan.FromSeconds(30)) { SetDisconnect(_state.Peer, "reliable_delivery_stalled"); continue; }
                        for (var _i = 0; _i < 128 && _state.Tokens >= 64; _i++)
                        {
                            Pending _pending = null;
                            var _reliable = _state.Reliable.Count > 0;
                            if (_reliable)
                            {
                                if (_state.Reliable.Peek().Bytes.Length > _state.Tokens) break;
                                _pending = _state.Reliable.Dequeue();
                            }
                            else if (_state.Realtime.Count > 0)
                            {
                                if (_state.Realtime.Peek().Bytes.Length > _state.Tokens) break;
                                _pending = _state.Realtime.Dequeue();
                            }
                            else foreach (var _entry in _state.Poses) { if (_entry.Value.Bytes.Length <= _state.Tokens) { _pending = _entry.Value; _state.Poses.Remove(_entry.Key); } break; }
                            if (_pending == null) break;
                            if (!_reliable)
                            {
                                _state.RealtimeBytes -= _pending.Bytes.Length;
                                if (_now - _pending.Time > TimeSpan.FromMilliseconds(250)) { Interlocked.Increment(ref _expired); continue; }
                            }
                            _state.Tokens -= _pending.Bytes.Length;
                            if (_reliable) _state.Peer.SendWithDeliveryEvent(_pending.Bytes, _pending.Lane, DeliveryMethod.ReliableOrdered, _pending.Bytes.Length);
                            else foreach (var _part in RealtimeFragments.GetSplit(_pending.Bytes, ++_state.FrameId)) _state.Peer.Send(_part, DeliveryMethod.Unreliable);
                        }
                    }
                }
                await Task.Delay(5, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception _exception)
        {
            foreach (var _state in _peers.Values) SetDisconnect(_state.Peer, "transport_sender_failed:" + _exception.GetType().Name);
        }
    }

    public static async Task<RelayPacket> GetQueryAsync(IPEndPoint endpoint, TimeSpan timeout)
    {
        using var _transport = new RelayTransport();
        _transport.SetUnconnected(endpoint, new RelayPacket { Type = "Query", ProtocolVersion = WireProtocol.Version });
        var _deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < _deadline)
        {
            if (_transport.GetTryReceive(out var _packet) && _packet.Endpoint.Equals(endpoint)) return _packet.Packet;
            await Task.Delay(10).ConfigureAwait(false);
        }
        throw new TimeoutException("The relay query timed out.");
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _manager.Stop();
        try { _sender.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        _peers.Clear();
        _inbox.SetClear();
        _stop.Dispose();
    }
}
