using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Refusion.Transport;

public sealed class RealtimeFragments
{
    private sealed class Buffer
    {
        public int Length;
        public byte[][] Parts;
        public int Count;
        public int Bytes;
        public DateTime Time = DateTime.UtcNow;
    }
    private readonly Dictionary<uint, Buffer> _buffers = new();
    private int _bytes;
    public long Expired { get; private set; }

    public static IEnumerable<byte[]> GetSplit(byte[] frame, uint id)
    {
        const int _partSize = 1100;
        var _count = (frame.Length + _partSize - 1) / _partSize;
        if (frame.Length > WireProtocol.MaxMessage || _count > 64) throw new InvalidDataException("realtime_frame_too_large");
        for (var _i = 0; _i < _count; _i++)
        {
            var _length = Math.Min(_partSize, frame.Length - _i * _partSize);
            var _part = new byte[_length + 16];
            BinaryPrimitives.WriteUInt32LittleEndian(_part, 0x46554652);
            BinaryPrimitives.WriteUInt32LittleEndian(_part.AsSpan(4), id);
            BinaryPrimitives.WriteInt32LittleEndian(_part.AsSpan(8), frame.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(_part.AsSpan(12), (ushort)_i);
            BinaryPrimitives.WriteUInt16LittleEndian(_part.AsSpan(14), (ushort)_count);
            Array.Copy(frame, _i * _partSize, _part, 16, _length);
            yield return _part;
        }
    }

    public byte[] GetAccept(byte[] part)
    {
        SetExpire();
        if (part.Length < 16 || part.Length > 1116 || BinaryPrimitives.ReadUInt32LittleEndian(part) != 0x46554652) throw new InvalidDataException("invalid_realtime_fragment");
        var _id = BinaryPrimitives.ReadUInt32LittleEndian(part.AsSpan(4));
        var _length = BinaryPrimitives.ReadInt32LittleEndian(part.AsSpan(8));
        var _index = BinaryPrimitives.ReadUInt16LittleEndian(part.AsSpan(12));
        var _count = BinaryPrimitives.ReadUInt16LittleEndian(part.AsSpan(14));
        if (_length < 6 || _length > WireProtocol.MaxMessage || _count < 1 || _count > 64 || _index >= _count || _count != (_length + 1099) / 1100 || part.Length - 16 != Math.Min(1100, _length - _index * 1100)) throw new InvalidDataException("invalid_realtime_fragment_bounds");
        if (!_buffers.TryGetValue(_id, out var _buffer))
        {
            if (_buffers.Count >= 32 || _bytes + _length > 2 * 1024 * 1024) { Expired++; return null; }
            _buffer = new Buffer { Length = _length, Parts = new byte[_count][] };
            _buffers.Add(_id, _buffer);
            _bytes += _length;
        }
        if (_buffer.Length != _length || _buffer.Parts.Length != _count) throw new InvalidDataException("conflicting_realtime_fragment");
        if (_buffer.Parts[_index] != null) return null;
        var _payload = new byte[part.Length - 16];
        Array.Copy(part, 16, _payload, 0, _payload.Length);
        _buffer.Parts[_index] = _payload;
        _buffer.Bytes += _payload.Length;
        _buffer.Count++;
        if (_buffer.Count != _count) return null;
        var _result = new byte[_length];
        var _offset = 0;
        foreach (var _value in _buffer.Parts) { Array.Copy(_value, 0, _result, _offset, _value.Length); _offset += _value.Length; }
        _buffers.Remove(_id);
        _bytes -= _length;
        return _result;
    }

    public void SetExpire()
    {
        var _stale = new List<uint>();
        foreach (var _entry in _buffers) if (DateTime.UtcNow - _entry.Value.Time > TimeSpan.FromMilliseconds(250)) _stale.Add(_entry.Key);
        foreach (var _id in _stale) { _bytes -= _buffers[_id].Length; _buffers.Remove(_id); Expired++; }
    }
}
