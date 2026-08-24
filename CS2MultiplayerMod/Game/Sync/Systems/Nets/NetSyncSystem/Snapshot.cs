using Colossal.Mathematics;
using Game.Net;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // One realize cycle's read-only view of the pools an incoming course can connect to, each with
    // the grid that makes a lookup local. Taken once per cycle and released in the same frame.
    public partial class NetSyncSystem
    {
        /// <summary>
        /// Take this cycle's connectable pools. <paramref name="ownedNodes"/> holds the building
        /// sub-net stubs a utility endpoint may connect to (see FindUtilityNodeAt); owned edges are
        /// only reachable through captured native intent.
        /// </summary>
        private void TakeNetSnapshot(out NodePool nodes, out EdgePool edges,
            out NodePool ownedNodes, out EdgePool ownedEdges)
        {
            nodes = NodePool.Take(_existingNodes);
            edges = EdgePool.Take(_existingEdges);
            ownedNodes = NodePool.Take(_ownedNodes);
            ownedEdges = EdgePool.Take(_ownedEdges);
            _rzCyclePool = nodes.Data.Length + edges.Curves.Length +
                           ownedNodes.Data.Length + ownedEdges.Curves.Length;
        }

        private struct NodePool : System.IDisposable
        {
            public NativeArray<Entity> Entities;
            public NativeArray<Node> Data;
            public NetCellIndex Index;

            public static NodePool Take(EntityQuery query)
            {
                var pool = new NodePool
                {
                    Entities = query.ToEntityArray(Allocator.Temp),
                    Data = query.ToComponentDataArray<Node>(Allocator.Temp),
                };
                var bounds = new NativeArray<float4>(pool.Data.Length, Allocator.Temp);
                try
                {
                    for (int i = 0; i < pool.Data.Length; i++)
                    {
                        float2 p = pool.Data[i].m_Position.xz;
                        bounds[i] = new float4(p, p);
                    }
                    pool.Index = NetCellIndex.Build(bounds);
                }
                finally
                {
                    bounds.Dispose();
                }
                return pool;
            }

            public void Dispose()
            {
                if (Entities.IsCreated) Entities.Dispose();
                if (Data.IsCreated) Data.Dispose();
                Index.Dispose();
            }
        }

        private struct EdgePool : System.IDisposable
        {
            public NativeArray<Entity> Entities;
            public NativeArray<Curve> Curves;
            public NetCellIndex Index;

            public static EdgePool Take(EntityQuery query)
            {
                var pool = new EdgePool
                {
                    Entities = query.ToEntityArray(Allocator.Temp),
                    Curves = query.ToComponentDataArray<Curve>(Allocator.Temp),
                };
                var bounds = new NativeArray<float4>(pool.Curves.Length, Allocator.Temp);
                try
                {
                    for (int i = 0; i < pool.Curves.Length; i++)
                    {
                        // The control hull contains the curve, so hull bounds never miss it.
                        Bezier4x3 b = pool.Curves[i].m_Bezier;
                        float2 lo = math.min(math.min(b.a.xz, b.b.xz), math.min(b.c.xz, b.d.xz));
                        float2 hi = math.max(math.max(b.a.xz, b.b.xz), math.max(b.c.xz, b.d.xz));
                        bounds[i] = new float4(lo, hi);
                    }
                    pool.Index = NetCellIndex.Build(bounds);
                }
                finally
                {
                    bounds.Dispose();
                }
                return pool;
            }

            public void Dispose()
            {
                if (Entities.IsCreated) Entities.Dispose();
                if (Curves.IsCreated) Curves.Dispose();
                Index.Dispose();
            }
        }
    }
}
