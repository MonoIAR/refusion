using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LabFusion.Data;
using LabFusion.Entities;
using LabFusion.Marrow.Extenders;
using LabFusion.Network.Serialization;
using LabFusion.Player;
using LabFusion.Scene;
using LabFusion.Senders;
using LabFusion.UI.Popups;
using LabFusion.Utilities;
using Refusion.Transport;
using Il2CppSLZ.Marrow.Interaction;

namespace LabFusion.Network;

public readonly struct WorldScope
{
    private readonly DedicatedServerNetworkLayer _layer;
    private readonly int _generation;
    private readonly Guid _session;
    private readonly uint _scene;
    public WorldScope(DedicatedServerNetworkLayer layer)
    {
        _layer = layer;
        _generation = layer?.ConnectionGeneration ?? 0;
        _session = layer?.SessionEpoch ?? Guid.Empty;
        _scene = layer?.SceneEpoch ?? 0;
    }
    public bool GetIsCurrent() => _layer == null || (ReferenceEquals(NetworkLayerManager.Layer, _layer) && _layer.ConnectionGeneration == _generation && _layer.SessionEpoch == _session && _layer.SceneEpoch == _scene && _layer.IsClient);
}

public sealed class WorldSyncData : INetSerializable
{
    public string Json;
    public int? GetSize() => Json.GetSize();
    public void Serialize(INetSerializer serializer) => serializer.SerializeValue(ref Json);
}

public sealed class WorldSyncMessage : NativeMessageHandler
{
    public override byte Tag => NativeMessageTag.WorldSync;
    protected override void OnHandleMessage(ReceivedMessage received)
    {
        var _data = received.ReadData<WorldSyncData>();
        var _command = JsonSerializer.Deserialize<CatchupCommand>(_data.Json, WireProtocol.Json);
        if (_command != null && _command.Entities.Count <= 1024 && _command.Entries.Count <= 128) WorldCatchup.SetHandle(received, _command);
    }
}

public static class WorldCatchup
{
    private sealed class Snapshot
    {
        public PlayerID Player;
        public CatchupLedger Ledger;
        public readonly Queue<ushort> Creation = new();
        public readonly HashSet<byte> Owners = new();
        public readonly PacketInbox Deltas = new();
        public bool Relations;
        public bool Data;
        public bool Barrier;
        public bool Flushing;
        public DateTime LastRepair = DateTime.UtcNow;
        public int Repairs;
    }
    private sealed class OwnerBatch
    {
        public uint Id;
        public byte Player;
        public readonly HashSet<ushort> Entities = new();
        public readonly HashSet<ushort> Missing = new();
        public bool Started;
        public DateTime Time = DateTime.UtcNow;
    }
    private static readonly Dictionary<byte, Snapshot> _snapshots = new();
    private static readonly Dictionary<(uint, byte), OwnerBatch> _ownerBatches = new();
    private static readonly HashSet<ushort> _tombstones = new();
    private static readonly HashSet<ushort> _pendingSpawns = new();
    private static readonly Dictionary<ushort, DateTime> _downloads = new();
    private static readonly Dictionary<ushort, CatchupEntry> _earlyMissing = new();
    private static CatchupLedger _client;
    private static uint _nextId;
    private static uint _sceneReady;
    private static bool _manifestDone;
    private static bool _scenePending = true;
    private static DateTime _sceneSince = DateTime.UtcNow;
    private static DateTime _lastRepair = DateTime.UtcNow;
    private static int _repairs;
    private static DateTime _lastDiagnostic = DateTime.MinValue;
    [ThreadStatic] private static uint _contextId;
    public static uint ContextId => _contextId;
    public static JoinStage Stage { get; private set; } = JoinStage.TransportAccepted;
    public static bool IsPending => NetworkInfo.IsClient && Stage is not JoinStage.Ready and not JoinStage.ReadyDegraded;
    public static uint GetScene() => (NetworkLayerManager.Layer as DedicatedServerNetworkLayer)?.SceneEpoch ?? 0;
    public static WorldScope GetScope() => new(NetworkLayerManager.Layer as DedicatedServerNetworkLayer);
    public static bool GetHasSnapshots() => _snapshots.Count > 0;
    public static bool GetIsTombstone(ushort id) => _tombstones.Contains(id);

    public static void SetInitialize()
    {
        MultiplayerHooking.OnDisconnected += SetReset;
        MultiplayerHooking.OnPlayerLeft += SetPlayerLeft;
    }

    public static void SetReset()
    {
        foreach (var _entity in NetworkEntityManager.IDManager.RegisteredEntities.IDEntityLookup.Values) _entity.SetClearDataCatchup();
        _snapshots.Clear();
        _ownerBatches.Clear();
        _pendingSpawns.Clear();
        _tombstones.Clear();
        _downloads.Clear();
        _earlyMissing.Clear();
        _client = null;
        _manifestDone = false;
        _scenePending = true;
        _sceneSince = DateTime.UtcNow;
        _sceneReady = 0;
        _contextId = 0;
        SetStage(JoinStage.TransportAccepted);
    }

    public static void SetStage(JoinStage stage)
    {
        Stage = stage;
        if (stage == JoinStage.FusionRegistered) { _scenePending = true; _sceneSince = DateTime.UtcNow; }
        if (NetworkLayerManager.Layer is DedicatedServerNetworkLayer _layer) _layer.SetJoinStage(stage.ToString(), _client?.Id ?? 0, _client?.Missing ?? 0);
    }

    public static void SetContext(uint id, Action action)
    {
        var _previous = _contextId;
        _contextId = id;
        try { action(); }
        finally { _contextId = _previous; }
    }

    private static void SetSend(PlayerID target, CatchupCommand command)
    {
        command.Scene = GetScene();
        var _data = new WorldSyncData { Json = JsonSerializer.Serialize(command, WireProtocol.Json) };
        if (NetworkInfo.IsHost)
        {
            using var _writer = NetWriter.Create(_data.GetSize());
            _data.Serialize(_writer);
            using var _message = NetMessage.Create(NativeMessageTag.WorldSync, _writer, CommonMessageRoutes.None);
            _message.CatchupId = command.Id;
            MessageSender.SendFromServer(target.SmallID, NetworkChannel.Reliable, _message);
        }
        else MessageRelay.RelayNative(_data, NativeMessageTag.WorldSync, CommonMessageRoutes.ReliableToServer);
    }

    public static void SetHandle(ReceivedMessage received, CatchupCommand command)
    {
        if (command.Scene != GetScene()) return;
        if (received.IsServerHandled)
        {
            if (!received.Sender.HasValue) return;
            var _player = PlayerIDManager.GetPlayerID(received.Sender.Value);
            if (PlayerID.IsNullOrInvalid(_player)) return;
            if (command.Operation == "SceneReady") { if (!FusionSceneManager.IsLoading()) SetStart(_player); return; }
            if (!_snapshots.TryGetValue(command.Player, out var _snapshot) || _snapshot.Ledger.Id != command.Id) return;
            if (command.Operation == "DataDone")
            {
                if (!_snapshot.Owners.Remove(_player.SmallID)) return;
                foreach (var _id in command.Missing)
                {
                    if (!_snapshot.Ledger.Entries.ContainsKey(_id)) continue;
                    _snapshot.Ledger.SetStatus(_id, CatchupStatus.Missing, "missing_content_dependency");
                }
                if (command.Missing.Count > 0) SetSend(_snapshot.Player, command);
                _snapshot.Ledger.SetProgress();
                return;
            }
            if (_player.SmallID != command.Player) return;
            if (command.Operation == "Created")
            {
                foreach (var _id in command.Entities) _snapshot.Ledger.SetStatus(_id, CatchupStatus.Created);
                foreach (var _id in command.Missing)
                {
                    if (_snapshot.Ledger.Entries.TryGetValue(_id, out var _entry) && (_entry.Kind is "Spawn" or "Relation")) _snapshot.Ledger.SetStatus(_id, CatchupStatus.Missing, command.Reason ?? "content_unavailable");
                    else FusionLogger.Warn($"Client {_player.SmallID} reported an invalid missing entity {_id}; ignoring.");
                }
            }
            else if (command.Operation == "Repair")
            {
                foreach (var _id in command.Entities.Take(1024)) if (_snapshot.Ledger.Entries.ContainsKey(_id)) _snapshot.Creation.Enqueue(_id);
            }
            else if (command.Operation == "Applied" && _snapshot.Barrier)
            {
                foreach (var _entry in _snapshot.Ledger.Entries.Values.Where(_entry => _entry.Status == CatchupStatus.Created).ToArray()) _snapshot.Ledger.SetStatus(_entry.Id, CatchupStatus.Applied);
                _snapshot.Flushing = true;
            }
            else if (command.Operation == "Progress") _snapshot.Ledger.SetProgress();
            return;
        }
        if (received.PlatformID.HasValue && PlayerIDManager.GetHostID()?.PlatformID != received.PlatformID.Value) return;
        if (command.Operation is "DataRequest" or "DataStart") { SetOwnerCommand(command); return; }
        if (command.Operation == "Begin")
        {
            if (_client?.Id == command.Id) return;
            _client = new CatchupLedger(command.Id, command.Scene);
            _manifestDone = false;
            _repairs = 0;
            _lastRepair = DateTime.UtcNow;
            SetStage(JoinStage.CatchingUp);
            return;
        }
        if (_client == null || _client.Id != command.Id) return;
        if (command.Operation == "Manifest")
        {
            foreach (var _entry in command.Entries) _client.SetAdd(_entry);
        }
        else if (command.Operation == "ManifestEnd")
        {
            _client.SetSeal(command.Expected);
            _manifestDone = true;
            foreach (var _entry in _earlyMissing.Values.ToArray()) SetContentMissing(_entry.Id, _entry.Barcode, _entry.Reason);
            foreach (var _id in _tombstones) _client.SetStatus(_id, CatchupStatus.Destroyed);
        }
        else if (command.Operation == "Tombstone") foreach (var _id in command.Entities) SetDestroyed(_id);
        else if (command.Operation == "DataDone") foreach (var _id in command.Missing) _client.SetStatus(_id, CatchupStatus.Missing, "missing_content_dependency");
        else if (command.Operation == "Barrier")
        {
            foreach (var _entry in _client.Entries.Values.Where(_entry => _entry.Status == CatchupStatus.Created).ToArray()) _client.SetStatus(_entry.Id, CatchupStatus.Applied);
            if (_client.Complete) SetSend(null, new CatchupCommand { Operation = "Applied", Id = _client.Id, Player = PlayerIDManager.LocalSmallID });
        }
        else if (command.Operation == "Complete" && _client.Complete)
        {
            SetStage(_client.Missing > 0 ? JoinStage.ReadyDegraded : JoinStage.Ready);
            FusionLogger.Log($"World catchup {_client.Id} complete: {_client.Applied} applied, {_client.Missing} missing, scene {GetScene()}.");
            if (_client.Missing > 0)
            {
                var _summary = string.Join("\n", _client.Entries.Values.Where(_entry => _entry.Status == CatchupStatus.Missing).Take(8).Select(_entry => $"{_entry.Id}: {_entry.Barcode ?? _entry.Kind} ({_entry.Reason})"));
                Notifier.Send(new Notification { Title = "Joined With Missing Content", Message = _summary, ShowPopup = true, SaveToMenu = true, PopupLength = 12f, Type = NotificationType.INFORMATION });
            }
        }
    }

    private static CatchupEntry GetEntry(NetworkEntity entity)
    {
        var _constraint = entity.GetExtender<NetworkConstraint>();
        var _player = entity.GetExtender<NetworkPlayer>();
        var _dependencies = new List<ushort>();
        if (_constraint != null)
        {
            foreach (var _go in new[] { _constraint.PointPair.go1, _constraint.PointPair.go2 })
            {
                if (_go == null) continue;
                var _marrow = _go.GetComponentInParent<MarrowEntity>();
                if (_marrow != null && IMarrowEntityExtender.Cache.TryGet(_marrow, out var _source)) _dependencies.Add(_source.ID);
            }
        }
        return new CatchupEntry { Id = entity.ID, Owner = entity.OwnerID?.SmallID ?? 0, Revision = entity.OwnerRevision, Kind = _player != null ? "Player" : _constraint != null ? "Relation" : entity.SpawnBarcode != null ? "Spawn" : "Scene", Barcode = entity.SpawnBarcode, Dependencies = _dependencies.Distinct().ToArray() };
    }

    private static void SetStart(PlayerID player)
    {
        if (_snapshots.ContainsKey(player.SmallID) || player.IsMe) return;
        var _snapshot = new Snapshot { Player = player, Ledger = new CatchupLedger(++_nextId, GetScene()) };
        foreach (var _entity in NetworkEntityManager.IDManager.RegisteredEntities.IDEntityLookup.Values.ToArray())
        {
            if (_entity.IsDestroyed || (!_entity.GetHasCreationCatchup() && _entity.GetExtender<NetworkPlayer>() == null)) continue;
            var _entry = GetEntry(_entity);
            _snapshot.Ledger.SetAdd(_entry);
            if (_entry.Kind != "Relation") _snapshot.Creation.Enqueue(_entry.Id);
        }
        _snapshot.Ledger.SetSeal(_snapshot.Ledger.Entries.Count);
        _snapshots[player.SmallID] = _snapshot;
        SetSend(player, new CatchupCommand { Operation = "Begin", Id = _snapshot.Ledger.Id });
        var _entries = _snapshot.Ledger.Entries.Values.ToArray();
        for (var _offset = 0; _offset < _entries.Length; _offset += 128) SetSend(player, new CatchupCommand { Operation = "Manifest", Id = _snapshot.Ledger.Id, Entries = _entries.Skip(_offset).Take(128).ToList() });
        SetSend(player, new CatchupCommand { Operation = "ManifestEnd", Id = _snapshot.Ledger.Id, Expected = _entries.Length });
        CatchupManager.InvokePlayerServerCatchup(player);
        FusionLogger.Log($"World catchup {_snapshot.Ledger.Id} started for {player.SmallID}: {_entries.Length} entities, scene {GetScene()}.");
    }

    public static bool GetTryBuffer(byte player, RelayPacket packet)
    {
        if (!_snapshots.TryGetValue(player, out var _snapshot) || packet.CatchupId == _snapshot.Ledger.Id || !FusionEnvelope.GetTryParse(packet.Payload, out var _prefix) || FusionEnvelope.GetIsSessionMessage(_prefix.Tag)) return false;
        if (_prefix.Route != 0 && (RelayType)_prefix.Route is not (RelayType.ToServer or RelayType.ToClients)) return false;
        if (!_snapshot.Deltas.SetEnqueue(new InboundPacket { Packet = packet, Endpoint = new IPEndPoint(IPAddress.Loopback, player), Size = packet.Payload.Length + 128 }))
        {
            FusionLogger.Warn($"World delta backlog overflow for player {player}.");
            NetworkLayerManager.Layer.DisconnectUser(_snapshot.Player.PlatformID);
        }
        return true;
    }

    public static void SetUpdate()
    {
        if (!NetworkInfo.HasServer) return;
        SetOwnerUpdate();
        if (NetworkInfo.IsHost) { SetHostUpdate(); return; }
        if (PlayerIDManager.LocalID == null) return;
        if (_sceneReady != GetScene() && FusionSceneManager.HasTargetLoaded() && FusionSceneManager.IsDelayedLoadDone() && LocalPlayer.GetNetworkPlayer()?.HasRig == true)
        {
            _sceneReady = GetScene();
            _scenePending = false;
            SetStage(JoinStage.SceneReady);
            SetSend(null, new CatchupCommand { Operation = "SceneReady", Player = PlayerIDManager.LocalSmallID });
            _lastRepair = DateTime.UtcNow;
        }
        if (_scenePending && DateTime.UtcNow - _sceneSince > TimeSpan.FromSeconds(180)) { SetFail("Target scene did not become ready within 180 seconds."); return; }
        if (_client == null)
        {
            if (_sceneReady != 0 && DateTime.UtcNow - _lastRepair > TimeSpan.FromSeconds(2)) { SetSend(null, new CatchupCommand { Operation = "SceneReady", Player = PlayerIDManager.LocalSmallID }); _lastRepair = DateTime.UtcNow; }
            return;
        }
        if (!_manifestDone || !IsPending) return;
        var _created = new List<ushort>();
        foreach (var _entry in _client.Entries.Values.Where(_entry => _entry.Status == CatchupStatus.Pending).ToArray())
        {
            if (_entry.Dependencies.Any(_id => _client.Entries.TryGetValue(_id, out var _dependency) && _dependency.Status == CatchupStatus.Missing)) { SetContentMissing(_entry.Id, _entry.Barcode, "missing_content_dependency"); continue; }
            var _entity = NetworkEntityManager.IDManager.RegisteredEntities.GetEntity(_entry.Id);
            if (_entity == null || !_entity.IsRegistered) continue;
            var _player = _entity.GetExtender<NetworkPlayer>();
            if (_player != null && !_player.HasRig) continue;
            if (_entry.Kind != "Relation")
            {
                var _owner = PlayerIDManager.GetPlayerID(_entry.Owner);
                if (_owner == null) continue;
                _entity.SetOwner(_owner, _entry.Revision);
            }
            if (_client.SetStatus(_entry.Id, CatchupStatus.Created)) _created.Add(_entry.Id);
            if (_created.Count >= 128) break;
        }
        if (_created.Count > 0) SetSend(null, new CatchupCommand { Operation = "Created", Id = _client.Id, Player = PlayerIDManager.LocalSmallID, Entities = _created });
        foreach (var _download in _downloads.ToArray())
        {
            if (DateTime.UtcNow - _download.Value > TimeSpan.FromSeconds(120)) SetContentMissing(_download.Key, _client.Entries.TryGetValue(_download.Key, out var _entry) ? _entry.Barcode : null, "download_no_progress");
        }
        if (_downloads.Count > 0 && DateTime.UtcNow - _lastRepair > TimeSpan.FromSeconds(2))
        {
            SetSend(null, new CatchupCommand { Operation = "Progress", Id = _client.Id, Player = PlayerIDManager.LocalSmallID });
            _lastRepair = DateTime.UtcNow;
            _client.SetProgress();
        }
        else if (DateTime.UtcNow - _client.LastProgress > TimeSpan.FromSeconds(2) && DateTime.UtcNow - _lastRepair > TimeSpan.FromSeconds(2) && _repairs < 3)
        {
            SetSend(null, new CatchupCommand { Operation = "Repair", Id = _client.Id, Player = PlayerIDManager.LocalSmallID, Entities = _client.Entries.Values.Where(_entry => _entry.Status == CatchupStatus.Pending).Take(1024).Select(_entry => _entry.Id).ToList() });
            _lastRepair = DateTime.UtcNow;
            _repairs++;
        }
        if (DateTime.UtcNow - _client.LastProgress > TimeSpan.FromSeconds(30)) SetFail($"World catchup {_client.Id} stalled; {_client.Entries.Values.Count(_entry => _entry.Status == CatchupStatus.Pending)} entities pending.");
    }

    private static void SetHostUpdate()
    {
        var _clock = Stopwatch.StartNew();
        foreach (var _snapshot in _snapshots.Values.ToArray())
        {
            if (!_snapshot.Player.IsValid) { _snapshots.Remove(_snapshot.Player.SmallID); continue; }
            if (DateTime.UtcNow - _snapshot.Ledger.LastProgress > TimeSpan.FromSeconds(30)) { FusionLogger.Warn($"World catchup {_snapshot.Ledger.Id} timed out for {_snapshot.Player.SmallID}."); NetworkLayerManager.Layer.DisconnectUser(_snapshot.Player.PlatformID); _snapshots.Remove(_snapshot.Player.SmallID); continue; }
            if ((NetworkLayerManager.Layer as DedicatedServerNetworkLayer)?.GetReliableBacklog() > 4 * 1024 * 1024) continue;
            for (var _i = 0; _i < 16 && _snapshot.Creation.Count > 0 && _clock.Elapsed.TotalMilliseconds < 2; _i++)
            {
                var _id = _snapshot.Creation.Dequeue();
                var _entity = NetworkEntityManager.IDManager.RegisteredEntities.GetEntity(_id);
                if (_entity == null) { SetDestroyed(_id); continue; }
                SetContext(_snapshot.Ledger.Id, () =>
                {
                    var _player = _entity.GetExtender<NetworkPlayer>();
                    if (_player != null)
                    {
                        ConnectionSender.SendPlayerCatchup(_snapshot.Player.PlatformID, _player.PlayerID);
                        ConnectionSender.SendPlayerProfileTo(_snapshot.Player.SmallID, _snapshot.Player.PlatformID, _player.PlayerID);
                        var _barcode = _player.PlayerID.IsMe ? RigData.RigAvatarId : _player.AvatarSetter.AvatarBarcode;
                        var _stats = _player.PlayerID.IsMe ? RigData.RigAvatarStats : _player.AvatarSetter.AvatarStats;
                        ConnectionSender.SendPlayerAvatarTo(_snapshot.Player.SmallID, _snapshot.Player.PlatformID, _player.PlayerID, _barcode, _stats);
                    }
                    else _entity.InvokeCreationCatchup(_snapshot.Player);
                });
            }
            if (!_snapshot.Relations && _snapshot.Creation.Count == 0 && _snapshot.Ledger.Entries.Values.Where(_entry => _entry.Kind != "Relation").All(_entry => _entry.Status != CatchupStatus.Pending))
            {
                _snapshot.Relations = true;
                foreach (var _entry in _snapshot.Ledger.Entries.Values.Where(_entry => _entry.Kind == "Relation"))
                {
                    if (_entry.Dependencies.Any(_id => _snapshot.Ledger.Entries.TryGetValue(_id, out var _dependency) && _dependency.Status == CatchupStatus.Missing)) _snapshot.Ledger.SetStatus(_entry.Id, CatchupStatus.Missing, "missing_content_dependency");
                    else _snapshot.Creation.Enqueue(_entry.Id);
                }
            }
            if (_snapshot.Ledger.Created && !_snapshot.Data) SetStartData(_snapshot);
            if (_snapshot.Data && _snapshot.Owners.Count == 0 && !_snapshot.Barrier)
            {
                _snapshot.Barrier = true;
                var _missing = _snapshot.Ledger.Entries.Values.Where(_entry => _entry.Status == CatchupStatus.Missing).Select(_entry => _entry.Id).ToList();
                SetSend(_snapshot.Player, new CatchupCommand { Operation = "DataDone", Id = _snapshot.Ledger.Id, Missing = _missing });
                SetSend(_snapshot.Player, new CatchupCommand { Operation = "Barrier", Id = _snapshot.Ledger.Id });
            }
            if (_snapshot.Flushing)
            {
                for (var _i = 0; _i < 64 && _snapshot.Deltas.GetTryDequeue(out var _delta); _i++) ((DedicatedServerNetworkLayer)NetworkLayerManager.Layer).SetSendBuffered(_delta.Packet);
                if (!_snapshot.Deltas.GetTryDequeue(out var _remaining))
                {
                    SetSend(_snapshot.Player, new CatchupCommand { Operation = "Complete", Id = _snapshot.Ledger.Id });
                    _snapshots.Remove(_snapshot.Player.SmallID);
                }
                else _snapshot.Deltas.SetEnqueue(_remaining);
            }
        }
    }

    private static void SetStartData(Snapshot snapshot)
    {
        snapshot.Data = true;
        var _groups = new Dictionary<byte, List<ushort>>();
        foreach (var _entry in snapshot.Ledger.Entries.Values.Where(_entry => _entry.Status == CatchupStatus.Created))
        {
            if (_entry.Kind == "Player" && _entry.Id == snapshot.Player.SmallID) continue;
            var _entity = NetworkEntityManager.IDManager.RegisteredEntities.GetEntity(_entry.Id);
            if (_entity == null) continue;
            var _owner = _entity.OwnerID ?? PlayerIDManager.GetHostID();
            if (_owner == null) { FusionLogger.Warn($"Entity {_entry.Id} has no authoritative owner during catchup; skipping its data."); continue; }
            if (!_groups.TryGetValue(_owner.SmallID, out var _ids)) _groups[_owner.SmallID] = _ids = new();
            _ids.Add(_entry.Id);
        }
        foreach (var _group in _groups)
        {
            snapshot.Owners.Add(_group.Key);
            var _owner = PlayerIDManager.GetPlayerID(_group.Key);
            for (var _offset = 0; _offset < _group.Value.Count; _offset += 128)
            {
                var _command = new CatchupCommand { Operation = "DataRequest", Id = snapshot.Ledger.Id, Player = snapshot.Player.SmallID, Entities = _group.Value.Skip(_offset).Take(128).ToList(), Missing = snapshot.Ledger.Entries.Values.Where(_entry => _entry.Status == CatchupStatus.Missing).Select(_entry => _entry.Id).Take(1024).ToList() };
                if (_owner.IsMe) SetOwnerCommand(_command); else SetSend(_owner, _command);
            }
            var _start = new CatchupCommand { Operation = "DataStart", Id = snapshot.Ledger.Id, Player = snapshot.Player.SmallID };
            if (_owner.IsMe) SetOwnerCommand(_start); else SetSend(_owner, _start);
        }
    }

    private static void SetOwnerCommand(CatchupCommand command)
    {
        var _key = (command.Id, command.Player);
        if (!_ownerBatches.TryGetValue(_key, out var _batch)) _ownerBatches[_key] = _batch = new OwnerBatch { Id = command.Id, Player = command.Player };
        foreach (var _id in command.Entities) _batch.Entities.Add(_id);
        foreach (var _id in command.Missing) _batch.Missing.Add(_id);
        if (command.Operation != "DataStart" || _batch.Started) return;
        _batch.Started = true;
        var _player = PlayerIDManager.GetPlayerID(command.Player);
        if (_player == null) return;
        SetContext(command.Id, () =>
        {
            foreach (var _id in _batch.Entities)
            {
                var _entity = NetworkEntityManager.IDManager.RegisteredEntities.GetEntity(_id);
                if (_entity == null || (_entity.OwnerID != null && !_entity.IsOwner)) continue;
                CatchupManager.InvokeEntityDataCatchup(_player, new NetworkEntityReference(_entity));
            }
        });
    }

    private static void SetOwnerUpdate()
    {
        foreach (var _batch in _ownerBatches.Values.ToArray())
        {
            if (!_batch.Started) continue;
            var _player = PlayerIDManager.GetPlayerID(_batch.Player);
            if (_player == null) { _ownerBatches.Remove((_batch.Id, _batch.Player)); continue; }
            var _pending = NetworkEntityManager.IDManager.RegisteredEntities.IDEntityLookup.Values.Where(_entity => _entity.GetHasPendingDataCatchup(_player)).ToArray();
            if (_pending.Length > 0 && _batch.Missing.Count == 0)
            {
                if (DateTime.UtcNow - _batch.Time > TimeSpan.FromSeconds(30)) FusionLogger.Warn($"Entity data dependencies did not finish for player {_batch.Player}; marking {_pending.Length} entities missing.");
                else continue;
            }
            var _missing = _pending.Select(_entity => _entity.ID).ToList();
            foreach (var _entity in _pending) _entity.SetCancelDataCatchup(_player);
            var _command = new CatchupCommand { Operation = "DataDone", Id = _batch.Id, Player = _batch.Player, Missing = _missing };
            if (NetworkInfo.IsHost)
            {
                if (_snapshots.TryGetValue(_batch.Player, out var _snapshot) && _snapshot.Ledger.Id == _batch.Id)
                {
                    _snapshot.Owners.Remove(PlayerIDManager.LocalSmallID);
                    foreach (var _id in _missing) _snapshot.Ledger.SetStatus(_id, CatchupStatus.Missing, "missing_content_dependency");
                }
            }
            else SetSend(null, _command);
            _ownerBatches.Remove((_batch.Id, _batch.Player));
        }
    }

    public static void SetDestroyed(ushort id)
    {
        if (NetworkInfo.IsClient)
        {
            if (_client == null || !_client.Entries.ContainsKey(id)) return;
            _tombstones.Add(id);
            _client.SetStatus(id, CatchupStatus.Destroyed);
        }
        else _tombstones.Add(id);
        _pendingSpawns.Remove(id);
        _downloads.Remove(id);
        foreach (var _snapshot in _snapshots.Values)
        {
            if (!_snapshot.Ledger.SetStatus(id, CatchupStatus.Destroyed)) continue;
            SetSend(_snapshot.Player, new CatchupCommand { Operation = "Tombstone", Id = _snapshot.Ledger.Id, Entities = new List<ushort> { id } });
        }
    }

    public static bool GetTryBeginSpawn(ushort id) => !_tombstones.Contains(id) && NetworkEntityManager.IDManager.RegisteredEntities.GetEntity(id) == null && _pendingSpawns.Add(id);
    public static void SetSpawnFinished(ushort id) { _pendingSpawns.Remove(id); _downloads.Remove(id); }
    public static void SetDownloading(ushort id) => _downloads[id] = DateTime.UtcNow;
    public static void SetDownloadProgress(ushort id) { if (_downloads.ContainsKey(id)) _downloads[id] = DateTime.UtcNow; }
    public static void SetContentMissing(ushort id, string barcode, string reason)
    {
        if (!NetworkInfo.IsClient) return;
        _downloads.Remove(id);
        _pendingSpawns.Remove(id);
        _earlyMissing[id] = new CatchupEntry { Id = id, Barcode = barcode, Reason = reason };
        if (_client == null || !_client.Entries.TryGetValue(id, out var _entry) || !_manifestDone) return;
        if (_entry.Kind is not "Spawn" and not "Relation") { SetFail($"Required scene entity {id} is unavailable: {reason}."); return; }
        if (_client.SetStatus(id, CatchupStatus.Missing, reason))
        {
            SetSend(null, new CatchupCommand { Operation = "Created", Id = _client.Id, Player = PlayerIDManager.LocalSmallID, Missing = new List<ushort> { id }, Reason = reason });
            FusionLogger.Warn($"World catchup missing entity {id}, barcode {barcode}: {reason}.");
        }
    }

    public static void SetFail(string reason)
    {
        FusionLogger.Warn(reason);
        NetworkHelper.Disconnect(reason);
    }

    public static void SetMessageFailed(ReceivedMessage received, string reason)
    {
        if (received.CatchupId != 0 && IsPending) SetFail("World catchup message failed: " + reason);
    }

    public static void SetMissingEntity(ushort id)
    {
        if (DateTime.UtcNow - _lastDiagnostic < TimeSpan.FromSeconds(5)) return;
        _lastDiagnostic = DateTime.UtcNow;
        FusionLogger.Warn($"Unknown network entity {id}; world stage {Stage}, scene {GetScene()}.");
        if (_client != null && IsPending) SetSend(null, new CatchupCommand { Operation = "Repair", Id = _client.Id, Player = PlayerIDManager.LocalSmallID, Entities = new List<ushort> { id } });
    }

    private static void SetPlayerLeft(PlayerID player)
    {
        _snapshots.Remove(player.SmallID);
        foreach (var _key in _ownerBatches.Keys.Where(_key => _key.Item2 == player.SmallID).ToArray()) _ownerBatches.Remove(_key);
        if (!NetworkInfo.IsHost) return;
        foreach (var _snapshot in _snapshots.Values)
        {
            if (!_snapshot.Owners.Remove(player.SmallID)) continue;
            _snapshot.Data = false;
            _snapshot.Barrier = false;
            _snapshot.Owners.Clear();
            _snapshot.Ledger.SetProgress();
        }
    }
}
