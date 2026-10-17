using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Refusion.Transport;

public static class WireProtocol
{
    public const int Version = 3;
    public const string Key = "Refusion/3";
    public const int Mtu = 1200;
    public const int MaxMessage = 65536;
    public const int MaxReliableBytes = 8 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
    private static readonly string[] _types = { "RawForward", "ServerMessage", "ClientMessage" };
    private static readonly string[] _modes = { "ClientMessage", "ServerBroadcast", "ServerTarget", "ServerBroadcastExcept" };

    public static byte[] GetEncode(RelayPacket packet)
    {
        using var _stream = new MemoryStream();
        using var _writer = new BinaryWriter(_stream, Encoding.UTF8, true);
        _writer.Write(0x33554652u);
        if (packet.Payload == null)
        {
            _writer.Write((byte)0);
            var _json = JsonSerializer.SerializeToUtf8Bytes(packet, Json);
            _writer.Write(_json);
        }
        else
        {
            if (!FusionEnvelope.GetTryParse(packet.Payload, out _)) throw new InvalidDataException("invalid_fusion_prefix");
            _writer.Write((byte)1);
            _writer.Write((byte)Array.IndexOf(_types, packet.Type));
            _writer.Write((byte)Math.Max(0, Array.IndexOf(_modes, packet.Mode ?? "ClientMessage")));
            _writer.Write(packet.SessionEpoch.ToByteArray());
            _writer.Write(packet.SceneEpoch);
            _writer.Write(packet.CatchupId);
            _writer.Write(packet.Sequence);
            _writer.Write(packet.ClientId);
            _writer.Write(packet.SmallId);
            _writer.Write(packet.PlatformId);
            _writer.Write(packet.TargetSmallId);
            _writer.Write(packet.ExcludeSmallId);
            _writer.Write((byte)packet.Channel);
            _writer.Write(packet.IsServerHandled);
            _writer.Write(packet.IsOperator);
            _writer.Write(packet.SessionToken ?? string.Empty);
            _writer.Write(packet.Payload.Length);
            _writer.Write(packet.Payload);
        }
        if (_stream.Length > MaxMessage) throw new InvalidDataException("frame_too_large");
        return _stream.ToArray();
    }

    public static RelayPacket GetDecode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 6 || bytes.Length > MaxMessage) throw new InvalidDataException("invalid_frame_length");
        using var _stream = new MemoryStream(bytes, false);
        using var _reader = new BinaryReader(_stream, Encoding.UTF8, true);
        if (_reader.ReadUInt32() != 0x33554652u) throw new InvalidDataException("protocol_mismatch");
        var _kind = _reader.ReadByte();
        if (_kind == 0) return JsonSerializer.Deserialize<RelayPacket>(_reader.ReadBytes(bytes.Length - 5), Json) ?? throw new InvalidDataException("invalid_control");
        if (_kind != 1) throw new InvalidDataException("invalid_frame_kind");
        var _type = _reader.ReadByte();
        var _mode = _reader.ReadByte();
        if (_type >= _types.Length || _mode >= _modes.Length) throw new InvalidDataException("invalid_operation");
        var _packet = new RelayPacket { Type = _types[_type], Mode = _modes[_mode], SessionEpoch = new Guid(_reader.ReadBytes(16)), SceneEpoch = _reader.ReadUInt32(), CatchupId = _reader.ReadUInt32(), Sequence = _reader.ReadUInt32(), ClientId = _reader.ReadInt32(), SmallId = _reader.ReadByte(), PlatformId = _reader.ReadUInt64(), TargetSmallId = _reader.ReadByte(), ExcludeSmallId = _reader.ReadByte(), Channel = _reader.ReadByte(), IsServerHandled = _reader.ReadBoolean(), IsOperator = _reader.ReadBoolean(), SessionToken = _reader.ReadString() };
        var _length = _reader.ReadInt32();
        if (_length < 0 || _length != _stream.Length - _stream.Position) throw new InvalidDataException("invalid_payload_length");
        _packet.Payload = _reader.ReadBytes(_length);
        if (!FusionEnvelope.GetTryParse(_packet.Payload, out var _prefix) || (_prefix.Route != 0 && _prefix.Channel != _packet.Channel)) throw new InvalidDataException("invalid_route_channel");
        return _packet;
    }
}

public readonly struct FusionEnvelope
{
    public byte Tag { get; init; }
    public byte Route { get; init; }
    public byte Channel { get; init; }
    public byte? Sender { get; init; }
    public int Offset { get; init; }
    public static bool GetTryParse(byte[] bytes, out FusionEnvelope prefix)
    {
        prefix = default;
        if (bytes == null || bytes.Length < 7 || bytes[1] > 5 || bytes[2] > 1) return false;
        var _offset = 3;
        if (bytes[1] == 4)
        {
            if (bytes.Length <= _offset || bytes[_offset++] != 1) return false;
            _offset++;
        }
        if (bytes[1] == 5)
        {
            if (bytes.Length < _offset + 4) return false;
            var _count = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(_offset, 4));
            if (_count < 0 || _count > 255) return false;
            _offset += 4 + _count;
        }
        byte? _sender = null;
        if (bytes[1] != 0)
        {
            if (bytes.Length <= _offset || bytes[_offset++] != 1 || bytes.Length <= _offset) return false;
            _sender = bytes[_offset++];
        }
        if (bytes.Length < _offset + 4) return false;
        var _length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(_offset, 4));
        _offset += 4;
        if (_length < 0 || _length != bytes.Length - _offset) return false;
        prefix = new FusionEnvelope { Tag = bytes[0], Route = bytes[1], Channel = bytes[2], Sender = _sender, Offset = _offset };
        return true;
    }

    public static string GetStateKey(RelayPacket packet)
    {
        if (!GetTryParse(packet.Payload, out var _p)) return null;
        if (_p.Tag == 4) return $"{packet.SessionEpoch}/{packet.SceneEpoch}/{_p.Sender}/4";
        if (_p.Tag == 17 && packet.Payload.Length >= _p.Offset + 2) return $"{packet.SessionEpoch}/{packet.SceneEpoch}/{_p.Sender}/17/{BinaryPrimitives.ReadUInt16BigEndian(packet.Payload.AsSpan(_p.Offset, 2))}";
        return null;
    }

    public static bool GetIsSessionMessage(byte tag) => tag is 1 or 2 or 3 or 5 or 12 or 44 or 45 or 59 or 60 or 67 or 77 or 78 or 201 or 216 or 218;
    public static bool GetIsNewer(uint value, uint previous) => unchecked((int)(value - previous)) > 0;
}
