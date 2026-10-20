using System;
using Colossal.Mathematics;
using Game.Net;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Vertical profile of a geometry-only fallback edge: the receiver regenerates the deck between its
    // two committed ends from its own terrain and water, so a straight committed deck is pinned to them.
    // Native courses need none of this; they are the tool's own unsplit input.
    public partial class NetSyncSystem
    {
        private float[] _profileDepth = new float[NetWaterProfilePin.ProbeCount];
        private float[] _profileDistance = new float[NetWaterProfilePin.ProbeCount];
        private float[] _profileDeck = new float[NetWaterProfilePin.ProbeCount];
        private readonly int[] _profileBreaks = new int[NetWaterProfilePin.MaxPieces + 1];

        // Capture: pinned, already banded. Realize: committed and refused pins (worth chasing).
        private int _capPinnedSpans;
        private int _capSelfAnchoredSpans;
        private int _rzPinnedSpans, _rzPinRefused;

        /// <summary>A geometry-only committed edge is pinned when its deck is straight between its nodes.</summary>
        private bool ShouldPinCommittedEdge(Entity prefab, Bezier4x3 curve,
            float2 startElevation, float2 endElevation)
        {
            NetPrefabInfo info = NetInfoOf(prefab);
            if (!NetWaterProfilePin.NeedsPin(startElevation.x, endElevation.x, info.ElevationLimit,
                    info.RequireElevated))
            {
                _capSelfAnchoredSpans++;
                return false;
            }
            if (!NetWaterProfilePin.IsEligible(true, true, info.ElevationLimit))
                return false;

            float lengthXZ = MathUtils.Length(curve.xz);
            if (!(lengthXZ > NetPlacementCommand.MinCourseLength)) return false;

            TerrainHeightData heightData = default;
            WaterSurfaceData<SurfaceWater> waterData = default;
            TakeSurfaceSnapshot(ref heightData, ref waterData);

            int probes = NetWaterProfilePin.ProbesFor(lengthXZ);
            if (probes == 0) return false;
            EnsureProfileCapacity(probes);
            float3 previous = curve.a;
            for (int i = 0; i < probes; i++)
            {
                float3 p = MathUtils.Position(curve, (float)i / (probes - 1));
                WaterUtils.SampleHeight(ref waterData, ref heightData, p, out float terrain, out float water,
                    out float depth);
                _profileDepth[i] = depth;
                _profileDeck[i] = p.y;
                _profileDistance[i] = i == 0 ? 0f : math.distance(previous.xz, p.xz);
                previous = p;
            }
            float tolerance = NetWaterProfilePin.CrossesWater(_profileDepth, probes)
                ? NetWaterProfilePin.ChordTolerance : NetWaterProfilePin.DryChordTolerance;
            return NetWaterProfilePin.Simplify(_profileDeck, _profileDistance, probes,
                tolerance, NetWaterProfilePin.MaxPieces, _profileBreaks) == 1;
        }

        private void EnsureProfileCapacity(int probes)
        {
            if (_profileDeck.Length >= probes) return;
            Array.Resize(ref _profileDepth, probes);
            Array.Resize(ref _profileDistance, probes);
            Array.Resize(ref _profileDeck, probes);
        }

        /// <summary>
        /// Moves the endpoint elevations to +/- the elevation limit, collapsing the generator's clamp band
        /// onto the endpoint heights, and drops free-height resolution. Splitting re-derives the real
        /// elevations afterwards.
        /// </summary>
        private void ApplyProfilePin(Entity prefab,
            ref uint startFlags, float startY, Entity startSnap, int startKind, float startT,
            ref float2 startElevation,
            ref uint endFlags, float endY, Entity endSnap, int endKind, float endT,
            ref float2 endElevation)
        {
            NetPrefabInfo info = NetInfoOf(prefab);
            bool startPinnable = EndpointPinnable(startSnap, startKind, startT, startY);
            bool endPinnable = EndpointPinnable(endSnap, endKind, endT, endY);
            if (!NetWaterProfilePin.IsEligible(startPinnable, endPinnable, info.ElevationLimit))
            {
                _rzPinRefused++;
                SyncLog.Detail(LogTopic.Nets, "NetSync profile pin refused: start[kind=" + startKind +
                    " sourceY=" + startY.ToString("F2") + " pinnable=" + startPinnable +
                    "] end[kind=" + endKind + " sourceY=" + endY.ToString("F2") +
                    " pinnable=" + endPinnable + "] range=" + info.ElevationRangeMin.ToString("F1") +
                    ".." + info.ElevationRangeMax.ToString("F1") +
                    " limit=" + info.ElevationLimit.ToString("F2"));
                return;
            }

            startFlags &= ~(uint)CoursePosFlags.FreeHeight;
            endFlags &= ~(uint)CoursePosFlags.FreeHeight;
            startElevation = NetWaterProfilePin.PinnedStartElevation(info.ElevationLimit);
            endElevation = NetWaterProfilePin.PinnedEndElevation(info.ElevationLimit);
            _rzPinnedSpans++;
        }

        /// <summary>
        /// A fresh node takes the transmitted height; existing geometry qualifies only at the source's height.
        /// </summary>
        private bool EndpointPinnable(Entity snap, int kind, float t, float sourceY)
        {
            if (kind == KindFree || kind == KindMergeBatch) return true;
            // Target went stale: a fresh node at the transmitted position.
            if (snap == Entity.Null) return true;
            if (!EntityManager.Exists(snap)) return false;

            float localY;
            if (kind == KindReuseNode || kind == KindReuseConnector)
            {
                if (!EntityManager.HasComponent<Node>(snap)) return false;
                localY = EntityManager.GetComponentData<Node>(snap).m_Position.y;
            }
            else if (kind == KindSplit)
            {
                if (!EntityManager.HasComponent<Curve>(snap)) return false;
                localY = MathUtils.Position(EntityManager.GetComponentData<Curve>(snap).m_Bezier,
                    math.clamp(t, 0f, 1f)).y;
            }
            else return false;

            return math.abs(localY - sourceY) <= SurfaceAgreementTol;
        }
    }
}
