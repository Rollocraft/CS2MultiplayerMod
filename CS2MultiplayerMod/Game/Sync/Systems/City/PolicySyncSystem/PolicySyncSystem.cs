using System.Collections.Generic;
using Game.Areas;
using Game.Buildings;
using Game.Common;
using Game.Policies;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Sync;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates district, transit and building policies by a 1 Hz scan of <see cref="Policy"/>
    /// buffers, applied through <c>PoliciesUISystem.SetPolicy</c>.
    /// </summary>
    public partial class PolicySyncSystem : CommandSyncSystem
    {
        private const long ScanIntervalMs = 1000;
        private const long TargetRetryWindowMs = 15000;
        private const int MaxPendingTargets = 256;

        private readonly ReplicationGuard _guard = new ReplicationGuard();
        private readonly LatestTargetRetryQueue<string, (EntityPolicyCommand cmd, int origin)> _targetRetry =
            new LatestTargetRetryQueue<string, (EntityPolicyCommand, int)>(MaxPendingTargets, TargetRetryWindowMs);

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private global::Game.UI.InGame.PoliciesUISystem _policiesUI;
        private EntityQuery _districts;
        private EntityQuery _routes;
        private EntityQuery _buildings;
        private EntityQuery _ownedUpgrades;

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

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _policiesUI = World.GetOrCreateSystemManaged<global::Game.UI.InGame.PoliciesUISystem>();

            _districts = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<District, Node, PrefabRef, Policy>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            _routes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Route, PrefabRef, Policy>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            _buildings = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, PrefabRef, global::Game.Objects.Transform, Policy>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });

            // Disabling an upgrade is its "Out of Service" policy on the owned upgrade entity, which the
            // building query (no Owner) misses; it shares the building target kind. Policy is not required:
            // the buffer only appears with the first toggle, which would otherwise have no baseline.
            _ownedUpgrades = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<PrefabRef, global::Game.Objects.Transform, Owner>(),
                Any = SyncQuery.ReadOnly<global::Game.Buildings.ServiceUpgrade, Extension>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            ListenFor(new[] { EntityPolicyCommand.Id });
        }

        protected override void DrainQueue()
        {
            _known.Clear();
            _next.Clear();
            _primed = false;
            _targetRetry.Clear();
            _guard.Clear();
            SyncInbox.Clear(_incoming);
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("PolicySync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady)
                {
                    DrainQueue();
                    return;
                }

                long now = service.NowMs;
                _guard.Prune(now);
                ApplyIncoming(session, now);

                if (now - _lastScanMs < ScanIntervalMs) return;
                _lastScanMs = now;
                Scan(session, now);
            }
        }

        // ---- Realize -----------------------------------------------------------

        private static string KindName(byte kind) =>
            kind == EntityPolicyCommand.KindDistrict ? "district" :
            kind == EntityPolicyCommand.KindRoute ? "line" : "building";

        private static string PolicyKey(string policyName, string targetName, float3 anchor) =>
            "pol|" + policyName + "|" + ReplicationGuard.Key(targetName, anchor);
    }
}
