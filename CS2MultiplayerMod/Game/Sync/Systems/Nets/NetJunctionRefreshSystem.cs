using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    /// <summary>
    /// Refreshes the junctions of edge-only updates before native references, composition and lanes,
    /// so junction lanes are not left behind.
    /// </summary>
    public partial class NetJunctionRefreshSystem : GameSystemBase
    {
        private EntityQuery _updatedEdges;
        private readonly HashSet<Entity> _nodes = new HashSet<Entity>();
        private int _refreshedNodes;
        private long _lastReportMs;

        protected override void OnCreate()
        {
            base.OnCreate();
            _updatedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Edge, Updated>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady)
            {
                _nodes.Clear();
                _refreshedNodes = 0;
                _lastReportMs = 0;
                return;
            }

            if (!_updatedEdges.IsEmptyIgnoreFilter)
            {
                NativeArray<Edge> edges = _updatedEdges.ToComponentDataArray<Edge>(Allocator.Temp);
                try { _refreshedNodes += NetJunctionRefresh.Refresh(EntityManager, edges, _nodes); }
                finally { edges.Dispose(); }
            }

            long now = service.NowMs;
            if (_refreshedNodes == 0 || now - _lastReportMs < 5000) return;
            SyncLog.Trace(LogTopic.Nets, "net junction refresh: included " + _refreshedNodes +
                " endpoint node(s) missing Updated from an edge refresh");
            _refreshedNodes = 0;
            _lastReportMs = now;
        }
    }
}
