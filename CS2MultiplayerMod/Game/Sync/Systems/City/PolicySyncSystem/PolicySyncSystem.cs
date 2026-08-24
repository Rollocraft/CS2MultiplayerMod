using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Areas;
using Game.Buildings;
using Game.Common;
using Game.Policies;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates per-entity policies (district, transit, building) via 1 Hz scan:
    /// detect <see cref="Policy"/> buffer changes, broadcast <see cref="EntityPolicyCommand"/>.
    /// Realize by resolving target (prefab + anchor) and calling <c>PoliciesUISystem.SetPolicy</c>.
    /// Echo guarded per-(target, policy).
    /// </summary>
    public partial class PolicySyncSystem : GameSystemBase
    {
        private const long ScanIntervalMs = 1000;
        private const long TargetRetryWindowMs = 15000;
        private const int MaxPendingTargets = 256;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();
        private readonly List<(EntityPolicyCommand cmd, int origin, long deadline)> _targetRetry =
            new List<(EntityPolicyCommand, int, long)>();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private global::Game.UI.InGame.PoliciesUISystem _policiesUI;
        private EntityQuery _districts;
        private EntityQuery _routes;
        private EntityQuery _buildings;
        private EntityQuery _ownedUpgrades;
        private CommandObserver _observer;

        /// <summary>The panel that toggles an upgrade finds this policy by name; so do we.</summary>
        private const string OutOfServicePolicyName = "Out of Service";
        private Entity _outOfServicePolicy;

        private Dictionary<Entity, List<PolicyEntry>> _known = new Dictionary<Entity, List<PolicyEntry>>();
        private Dictionary<Entity, List<PolicyEntry>> _next = new Dictionary<Entity, List<PolicyEntry>>();
        private bool _primed;
        private long _lastScanMs;

        private struct PolicyEntry
        {
            public Entity Policy;
            public bool Active;
            public float Adjustment;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(PolicySyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _policiesUI = World.GetOrCreateSystemManaged<global::Game.UI.InGame.PoliciesUISystem>();

            _districts = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<District>(),
                    ComponentType.ReadOnly<Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Policy>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            _routes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Policy>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });

            _buildings = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<global::Game.Objects.Transform>(),
                    ComponentType.ReadOnly<Policy>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            // Disabling a service upgrade is not a component edit: the game routes it through the
            // "Out of Service" policy on the upgrade entity itself. Those entities are owned by their
            // host building, so the building query above (which excludes Owner, to keep a sub-building
            // from answering for its parent) never saw them and the toggle never replicated. They are
            // identified the same way a building is - prefab plus position - so they share the
            // building target kind and need nothing new on the wire.
            //
            // Policy is deliberately NOT required here. An upgrade has no policy buffer until it is
            // first toggled, and the buffer appears in the same moment as the change: requiring it
            // meant the very first observation of the entity was already the changed state, so the
            // diff had nothing to compare against and the toggle was never sent.
            _ownedUpgrades = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<global::Game.Objects.Transform>(),
                    ComponentType.ReadOnly<Owner>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<global::Game.Buildings.ServiceUpgrade>(),
                    ComponentType.ReadOnly<Extension>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, EntityPolicyCommand.Id);
                Mod.Service.Session.AddObserver(_observer);
            }
        }

        protected override void OnDestroy()
        {
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady)
            {
                if (_known.Count > 0) { _known.Clear(); _primed = false; }
                _targetRetry.Clear();
                SyncInbox.Clear(_incoming);
                return;
            }

            long now = service.NowMs;
            _guard.Prune(now);
            ApplyIncoming(session, now);

            if (now - _lastScanMs < ScanIntervalMs) return;
            _lastScanMs = now;
            Scan(session, now);
        }

        // ---- Detect ------------------------------------------------------------





        // ---- Realize -----------------------------------------------------------





        private static string KindName(byte kind) =>
            kind == EntityPolicyCommand.KindDistrict ? "district" :
            kind == EntityPolicyCommand.KindRoute ? "line" : "building";

        private static string PolicyKey(string policyName, string targetName, float3 anchor) =>
            "pol|" + policyName + "|" + ReplicationGuard.Key(targetName, anchor);

    }
}
