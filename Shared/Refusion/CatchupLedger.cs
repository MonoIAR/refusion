using System;
using System.Collections.Generic;
using System.Linq;

namespace Refusion.Transport;

public enum JoinStage { TransportAccepted, FusionRegistered, SceneReady, CatchingUp, Ready, ReadyDegraded }
public enum CatchupStatus { Pending, Created, Applied, Missing, Destroyed }
public sealed class CatchupEntry
{
    public ushort Id { get; set; }
    public byte Owner { get; set; }
    public uint Revision { get; set; }
    public string Kind { get; set; }
    public string Barcode { get; set; }
    public ushort[] Dependencies { get; set; } = Array.Empty<ushort>();
    public CatchupStatus Status { get; set; }
    public string Reason { get; set; }
}

public sealed class CatchupCommand
{
    public string Operation { get; set; }
    public uint Id { get; set; }
    public uint Scene { get; set; }
    public byte Player { get; set; }
    public int Expected { get; set; }
    public int Phase { get; set; }
    public List<CatchupEntry> Entries { get; set; } = new();
    public List<ushort> Entities { get; set; } = new();
    public List<ushort> Missing { get; set; } = new();
    public string Reason { get; set; }
}

public sealed class CatchupLedger
{
    private readonly Dictionary<ushort, CatchupEntry> _entries = new();
    public IReadOnlyDictionary<ushort, CatchupEntry> Entries => _entries;
    public uint Id { get; }
    public uint Scene { get; }
    public DateTime LastProgress { get; private set; } = DateTime.UtcNow;
    public bool Sealed { get; private set; }
    public bool Complete => Sealed && _entries.Values.All(_entry => _entry.Status is CatchupStatus.Applied or CatchupStatus.Missing or CatchupStatus.Destroyed);
    public bool Created => Sealed && _entries.Values.All(_entry => _entry.Status != CatchupStatus.Pending);
    public int Missing => _entries.Values.Count(_entry => _entry.Status == CatchupStatus.Missing);
    public int Applied => _entries.Values.Count(_entry => _entry.Status == CatchupStatus.Applied);

    public CatchupLedger(uint id, uint scene) { Id = id; Scene = scene; }
    public void SetAdd(CatchupEntry entry)
    {
        if (Sealed || _entries.Count >= 65536 || _entries.ContainsKey(entry.Id)) throw new InvalidOperationException("invalid_catchup_manifest");
        _entries.Add(entry.Id, entry);
        LastProgress = DateTime.UtcNow;
    }
    public void SetSeal(int expected)
    {
        if (expected != _entries.Count) throw new InvalidOperationException("incomplete_catchup_manifest");
        Sealed = true;
        LastProgress = DateTime.UtcNow;
    }
    public bool SetStatus(ushort id, CatchupStatus status, string reason = null)
    {
        if (!_entries.TryGetValue(id, out var _entry) || _entry.Status == CatchupStatus.Destroyed || _entry.Status == status) return false;
        if (_entry.Status == CatchupStatus.Missing && status != CatchupStatus.Destroyed) return false;
        if (status == CatchupStatus.Pending || (_entry.Status == CatchupStatus.Applied && status == CatchupStatus.Created)) return false;
        _entry.Status = status;
        _entry.Reason = reason;
        LastProgress = DateTime.UtcNow;
        return true;
    }
    public void SetProgress() => LastProgress = DateTime.UtcNow;
}
