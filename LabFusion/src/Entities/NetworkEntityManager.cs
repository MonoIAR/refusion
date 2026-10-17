using LabFusion.Network;
using LabFusion.Player;
using LabFusion.Scene;
using LabFusion.Utilities;
using LabFusion.Data;

using MelonLoader;

namespace LabFusion.Entities;

public static class NetworkEntityManager
{
    private static readonly EntityIDManager<NetworkEntity> _idManager = new();
    public static EntityIDManager<NetworkEntity> IDManager => _idManager;

    private static readonly EntityUpdatableManager _updatableManager = new();
    public static EntityUpdatableManager UpdatableManager => _updatableManager;

    private static readonly EntityValidationList _ownershipTransferValidators = new();
    public static EntityValidationList OwnershipTransferValidators => _ownershipTransferValidators;

    private static readonly Dictionary<ushort, List<NetworkEntityDelegate>> _entityRegisteredCallbacks = new();

    public static void OnInitializeManager()
    {
        IDManager.OnEntityRegistered += OnEntityRegistered;
        IDManager.OnEntityUnregistered += OnEntityUnregistered;
        MultiplayerHooking.OnPlayerLeft += OnPlayerLeft;
    }

    private static void OnEntityUnregistered(NetworkEntity entity)
    {
        WorldCatchup.SetDestroyed(entity.ID);
    }

    public static void OnCleanupIds()
    {
        IDManager.RegisteredEntities.ClearID();
        IDManager.QueuedEntities.ClearID();

        _entityRegisteredCallbacks.Clear();
    }

    public static void OnCleanupEntities()
    {
        // Clear registered entities
        var registeredEntities = IDManager.RegisteredEntities.EntityIDLookup.Keys.ToList();

        foreach (var entity in registeredEntities)
        {
            try
            {
                IDManager.UnregisterEntity(entity);
            }
            catch (Exception e)
            {
                FusionLogger.LogException("unregistering NetworkEntity", e);
            }
        }

        IDManager.RegisteredEntities.Clear();

        // Clear queued entities
        var queuedEntities = IDManager.QueuedEntities.EntityIDLookup.Keys.ToList();

        foreach (var entity in queuedEntities)
        {
            try
            {
                entity.Unregister();
            }
            catch (Exception e)
            {
                FusionLogger.LogException("unregistering queued NetworkEntity", e);
            }
        }

        IDManager.QueuedEntities.Clear();

        _entityRegisteredCallbacks.Clear();
    }

    private static void OnEntityRegistered(NetworkEntity entity)
    {
        var id = entity.ID;

        if (_entityRegisteredCallbacks.TryGetValue(id, out var callbacks))
        {
            foreach (var callback in callbacks)
            {
                try
                {
                    callback(entity);
                }
                catch (Exception e)
                {
                    FusionLogger.LogException("executing NetworkEntityManager.EntityRegisteredCallback", e);
                }
            }

            _entityRegisteredCallbacks.Remove(id);
        }
    }

    private static void OnPlayerLeft(PlayerID playerID)
    {
        foreach (var entity in IDManager.RegisteredEntities.IDEntityLookup.Values)
        {
            entity.OnPlayerLeft(playerID);
        }
    }

    public static void OnUpdate(float deltaTime)
    {
        UpdatableManager.OnEntityUpdate(deltaTime);
    }

    public static void OnFixedUpdate(float deltaTime)
    {
        UpdatableManager.OnEntityFixedUpdate(deltaTime);
    }

    public static void OnLateUpdate(float deltaTime)
    {
        UpdatableManager.OnEntityLateUpdate(deltaTime);
    }

    public static void RequestUnqueue(ushort queuedID)
    {
        if (!NetworkInfo.HasServer)
        {
            return;
        }

        var data = EntityUnqueueRequestData.Create(PlayerIDManager.LocalSmallID, queuedID);

        MessageRelay.RelayNative(data, NativeMessageTag.EntityUnqueueRequest, CommonMessageRoutes.ReliableToServer);
    }

    public static void TransferOwnership(NetworkEntity entity, PlayerID ownerID)
    {
        if (!OwnershipTransferValidators.Validate(entity, ownerID))
        {
#if DEBUG
            FusionLogger.Log($"Prevented ownership transfer for NetworkEntity at ID {entity.ID} due to failed validation!");
#endif

            return;
        }

        if (entity.IsOwnerLocked)
        {
            FusionLogger.Warn($"Attempted to transfer ownership for NetworkEntity at ID {entity.ID}, but ownership was locked!");
            return;
        }

        if (!entity.IsRegistered)
        {
            FusionLogger.Warn($"Attempted to transfer ownership for NetworkEntity at ID {entity.ID}, but it wasn't registered!");
            return;
        }

        var request = new EntityPlayerData()
        {
            PlayerID = ownerID.SmallID,
            Entity = new(entity),
        };

        MessageRelay.RelayNative(request, NativeMessageTag.EntityOwnershipRequest, CommonMessageRoutes.ReliableToServer);
    }

    public static void TakeOwnership(NetworkEntity entity)
    {
        // Don't allow taking ownership while interaction is disabled
        if (LocalControls.DisableInteraction)
        {
            return;
        }

        TransferOwnership(entity, PlayerIDManager.LocalID);
    }

    public static void HookEntityRegistered(ushort id, NetworkEntityDelegate callback)
    {
        var entity = IDManager.RegisteredEntities.GetEntity(id);

        if (entity != null)
        {
            callback(entity);
        }
        else
        {
            if (!_entityRegisteredCallbacks.ContainsKey(id))
            {
                _entityRegisteredCallbacks[id] = new();
            }

            _entityRegisteredCallbacks[id].Add(callback);
        }
    }
}