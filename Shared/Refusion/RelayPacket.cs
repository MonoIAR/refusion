using System;
using System.Text.Json.Serialization;

namespace Refusion.Transport;

public sealed class RelayPacket
{
    public Guid SessionEpoch { get; set; }
    public uint SceneEpoch { get; set; }
    public uint Sequence { get; set; }
    public uint CatchupId { get; set; }
    public string JoinStage { get; set; }
    public int MissingCount { get; set; }
    public int ClientBytesPerSecond { get; set; }
    public int HostBytesPerSecond { get; set; }
    public string Type { get; set; } = string.Empty;
    public int ClientId { get; set; }
    public byte SmallId { get; set; }
    public byte PreviousSmallId { get; set; }
    public int OwnerClientId { get; set; }
    public int TargetClientId { get; set; }
    public int ExcludeClientId { get; set; }
    public byte TargetSmallId { get; set; }
    public byte ExcludeSmallId { get; set; }
    public int ProtocolVersion { get; set; }
    public bool OwnerChanged { get; set; }
    public bool SessionResetRequired { get; set; }
    public bool IsServerHandled { get; set; }
    public int Channel { get; set; }
    public ulong PlatformId { get; set; }
    public ulong TargetPlatformId { get; set; }
    public string? ServerId { get; set; }
    public string? SessionToken { get; set; }
    public string? LevelBarcode { get; set; }
    public string? LoadingScreenBarcode { get; set; }
    public bool IsLoading { get; set; }
    public bool AnticheatEnabled { get; set; }
    public bool IsOperator { get; set; }
    public int PlayerCount { get; set; }
    public int MaxPlayers { get; set; }
    public string? ServerName { get; set; }
    public string? HostName { get; set; }
    public string? GamemodeTitle { get; set; }
    public string? GamemodeBarcode { get; set; }
    public string? ServerVersion { get; set; }
    public string? Name { get; set; }
    public string? Reason { get; set; }
    public string? PersistentPlayerId { get; set; }
    public string? Mode { get; set; }
    [JsonIgnore]
    public byte[] Payload { get; set; }
    public string? SettingsJson { get; set; }
    public string? Command { get; set; }
    public string? CommandArgs { get; set; }
    public string? RequestId { get; set; }
}

