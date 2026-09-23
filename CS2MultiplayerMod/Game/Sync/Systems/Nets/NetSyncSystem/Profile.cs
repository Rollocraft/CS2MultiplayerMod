using System;
using System.Collections.Generic;
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
    // Vertical profile: the deck between endpoints is regenerated from each machine's own terrain and
    // water, so capture pins a straight deck to its transmitted endpoint heights.
    public partial class NetSyncSystem
    {
        private float[] _profileSurface = new float[NetWaterProfilePin.ProbeCount];
        private float[] _profileTerrain = new float[NetWaterProfilePin.ProbeCount];
        private float[] _profileDepth = new float[NetWaterProfilePin.ProbeCount];
        private float[] _profileDistance = new float[NetWaterProfilePin.ProbeCount];
        private float[] _profileDeck = new float[NetWaterProfilePin.ProbeCount];
        private readonly int[] _profileBreaks = new int[NetWaterProfilePin.MaxPieces + 1];

        // Capture: pinned, already banded, unpinnable. Realize: committed and refused pins (worth chasing).
        private int _capPinnedSpans;
        private int _capSelfAnchoredSpans;
        private int _capBentDeckSpans;
        private int _rzPinnedSpans, _rzPinRefused;

        // Per-course trace lines for water crossings only, a handful per operation.
        private const int MaxProfileTraceLines = 6;
        private int _profileTraceLines;
        private bool _profileTouchedWater;
        private bool _profileDeckReady;
        private bool _profileTraceWorthy;
        private string _profileVerdict;

        /// <summary>Pins every straight-deck course once, at publish.</summary>
        private void PinStraightProfiles(List<NetPlacementCommand> courses)
        {
            _profileTraceLines = 0;
            for (int i = 0; i < courses.Count; i++) PinCapturedCourse(courses[i]);
        }

        private void PinStraightProfiles(List<LocalNetToolOperationItem> items)
        {
            _profileTraceLines = 0;
            for (int i = 0; i < items.Count; i++)
            {
                LocalNetToolOperationItem item = items[i];
                if (item.CommandId != NetPlacementCommand.Id || item.Placement == null) continue;
                PinCapturedCourse(item.Placement);
            }
        }

        /// <summary>
        /// Pins a course whose deck is the straight line between its endpoints (only a flag travels). Bent
        /// decks are left alone: splitting them would add nodes the source does not have.
        /// </summary>
        private void PinCapturedCourse(NetPlacementCommand command)
        {
            _profileTouchedWater = false;
            _profileDeckReady = false;
            _profileTraceWorthy = false;
            _profileVerdict = null;

            bool pin = false;
            if (MeasurePinnableDeck(command, out int probes, out int pieces))
            {
                if (pieces == 1)
                {
                    pin = true;
                    _profileVerdict = "pin";
                }
                else
                {
                    _capBentDeckSpans++;
                    _profileVerdict = "deck needs " + pieces + " chords";
                }
            }
            TraceMeasuredSpan(command, probes);

            if (!pin) return;
            command.PinProfile = true;
            _capPinnedSpans++;
        }

        /// <summary>The inputs of one water-crossing deck; compare both machines' logs.</summary>
        private void TraceMeasuredSpan(NetPlacementCommand command, int probes)
        {
            if (!_profileTraceWorthy || _profileTraceLines >= MaxProfileTraceLines) return;
            _profileTraceLines++;

            float deckLow = 0f, deckHigh = 0f;
            float deckStart = command.Start.PosY, deckEnd = command.End.PosY;
            if (_profileDeckReady)
            {
                deckLow = float.MaxValue;
                deckHigh = float.MinValue;
                for (int i = 0; i < probes; i++)
                {
                    if (_profileDeck[i] < deckLow) deckLow = _profileDeck[i];
                    if (_profileDeck[i] > deckHigh) deckHigh = _profileDeck[i];
                }
                deckStart = _profileDeck[0];
                deckEnd = _profileDeck[probes - 1];
            }

            NetPrefabInfo info = _prefabIndex.TryResolve(command.PrefabName, out Entity prefab)
                ? NetInfoOf(prefab) : default(NetPrefabInfo);
            var startFlags = (CoursePosFlags)command.Start.Flags;
            var endFlags = (CoursePosFlags)command.End.Flags;

            SyncLog.Detail(LogTopic.Nets, "NetSync water span '" + command.PrefabName +
                "' limit=" + info.ElevationLimit.ToString("F2") +
                " slope=" + info.MaxSlopeSteepness.ToString("F2") +
                " probes=" + probes +
                " start[" + (((startFlags & CoursePosFlags.FreeHeight) != 0) ? "free" : "fixed") +
                " elev=" + command.Start.ElevationLeft.ToString("F2") +
                " wireY=" + command.Start.PosY.ToString("F2") +
                (_profileTouchedWater ? " surface=" + _profileSurface[0].ToString("F2") : "") +
                " ->Y=" + deckStart.ToString("F2") + "]" +
                " end[" + (((endFlags & CoursePosFlags.FreeHeight) != 0) ? "free" : "fixed") +
                " elev=" + command.End.ElevationLeft.ToString("F2") +
                " wireY=" + command.End.PosY.ToString("F2") +
                (_profileTouchedWater
                    ? " surface=" + _profileSurface[probes - 1].ToString("F2") : "") +
                " ->Y=" + deckEnd.ToString("F2") + "]" +
                (_profileDeckReady
                    ? " deck=" + deckLow.ToString("F2") + ".." + deckHigh.ToString("F2")
                    : "") +
                " -> " + (_profileVerdict ?? "not pinnable"));
        }

        /// <summary>
        /// Samples the deck being committed into <see cref="_profileDeck"/> and reports how many straight
        /// pieces hold it within tolerance.
        /// </summary>
        private bool MeasurePinnableDeck(NetPlacementCommand command, out int probes, out int pieces)
        {
            probes = 0;
            pieces = 0;
            if (!command.HasNativeCourse || command.PinProfile) return false;
            _profileVerdict = null;

            // Fixed-element nets divide themselves; owned sub-nets and tunnels do not depend on water.
            if (command.FixedIndex >= 0) { _profileVerdict = "fixed-element net"; return false; }
            if (command.Start.ParentMesh >= 0 || command.End.ParentMesh >= 0)
            { _profileVerdict = "owned sub-net"; return false; }
            if (math.max(command.Start.ElevationLeft, command.End.ElevationLeft) < -1f)
            { _profileVerdict = "tunnel"; return false; }

            if (!_prefabIndex.TryResolve(command.PrefabName, out Entity prefab))
            { _profileVerdict = "unknown prefab"; return false; }
            NetPrefabInfo info = NetInfoOf(prefab);

            // Skip only when both bounds already pin fixed endpoint heights.
            if (!NetWaterProfilePin.NeedsPin(command.Start.ElevationLeft, command.End.ElevationLeft,
                    info.ElevationLimit, info.RequireElevated,
                    ((CoursePosFlags)command.Start.Flags & CoursePosFlags.FreeHeight) != 0,
                    ((CoursePosFlags)command.End.Flags & CoursePosFlags.FreeHeight) != 0))
            {
                _capSelfAnchoredSpans++;
                _profileVerdict = "own elevation pins the deck";
                _profileTraceWorthy = true;
                return false;
            }

            if (!NetWaterProfilePin.IsEligible(true, true, info.ElevationLimit))
            { _profileVerdict = "invalid profile elevation limit"; return false; }

            float2 range = new float2(command.Start.CourseDelta, command.End.CourseDelta);
            if (!math.isfinite(range.x) || !math.isfinite(range.y) || range.y <= range.x) return false;

            Bezier4x3 source = SourceCurve(command);
            float lengthXZ = MathUtils.Length(MathUtils.Cut(source, range).xz);
            if (!(lengthXZ > NetPlacementCommand.MinCourseLength)) return false;

            var startPosition = new float3(command.Start.PosX, command.Start.PosY, command.Start.PosZ);
            var endPosition = new float3(command.End.PosX, command.End.PosY, command.End.PosZ);

            TerrainHeightData heightData = default;
            WaterSurfaceData<SurfaceWater> waterData = default;
            TakeSurfaceSnapshot(ref heightData, ref waterData);

            probes = NetWaterProfilePin.ProbesFor(lengthXZ);
            if (probes == 0) { _profileVerdict = "span exceeds profile probe limit"; return false; }
            EnsureProfileCapacity(probes);
            float3 previous = startPosition;
            for (int i = 0; i < probes; i++)
            {
                float t = math.lerp(range.x, range.y, (float)i / (probes - 1));
                float3 p = i == 0 ? startPosition
                    : i == probes - 1 ? endPosition
                    : MathUtils.Position(source, t);

                WaterUtils.SampleHeight(ref waterData, ref heightData, p, out float terrain, out float water,
                    out float depth);

                _profileTerrain[i] = terrain;
                _profileDepth[i] = depth;
                _profileSurface[i] = NetWaterProfilePin.SurfaceHeight(terrain, water, depth,
                    info.ElevationLimit);
                _profileDistance[i] = i == 0 ? 0f : math.distance(previous.xz, p.xz);
                previous = p;
            }

            _profileTouchedWater = NetWaterProfilePin.CrossesWater(_profileDepth, probes);
            _profileTraceWorthy = _profileTouchedWater;

            // The wire heights, not surface + elevation: over water the elevation is measured from terrain,
            // so adding the water surface counts the lake's depth twice.
            float startHeight = startPosition.y;
            float endHeight = endPosition.y;
            if (!math.isfinite(startHeight) || !math.isfinite(endHeight))
            { _profileVerdict = "endpoint height is not finite"; return false; }

            NetWaterProfilePin.PredictDeck(_profileSurface, _profileTerrain, _profileDistance, probes,
                startHeight, endHeight, command.Start.ElevationLeft, command.End.ElevationLeft,
                info.ElevationLimit, info.MaxSlopeSteepness, _profileDeck, info.RequireElevated);

            _profileDeckReady = true;
            float tolerance = _profileTouchedWater ? NetWaterProfilePin.ChordTolerance
                : NetWaterProfilePin.DryChordTolerance;
            pieces = NetWaterProfilePin.Simplify(_profileDeck, _profileDistance, probes,
                tolerance, NetWaterProfilePin.MaxPieces, _profileBreaks);
            return pieces >= 1;
        }

        /// <summary>A geometry-only committed edge is pinned when its deck is straight between its nodes.</summary>
        private bool ShouldPinCommittedEdge(Entity prefab, Bezier4x3 curve,
            float2 startElevation, float2 endElevation)
        {
            NetPrefabInfo info = NetInfoOf(prefab);
            if (!NetWaterProfilePin.NeedsPin(startElevation.x, endElevation.x, info.ElevationLimit,
                    info.RequireElevated, false, false))
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
            Array.Resize(ref _profileSurface, probes);
            Array.Resize(ref _profileTerrain, probes);
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

        private static Bezier4x3 SourceCurve(NetPlacementCommand command) => new Bezier4x3(
            new float3(command.Ax, command.Ay, command.Az),
            new float3(command.Bx, command.By, command.Bz),
            new float3(command.Cx, command.Cy, command.Cz),
            new float3(command.Dx, command.Dy, command.Dz));
    }
}
