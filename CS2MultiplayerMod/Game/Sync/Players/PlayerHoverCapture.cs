using System;
using Colossal.Mathematics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;
using Game.Common;
using Game.Input;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using ObjectTransform = Game.Objects.Transform;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    public partial class PlayerCursorSyncSystem
    {
        private ToolSystem _hoverTools;
        private ToolRaycastSystem _hoverRaycast;
        private PrefabSystem _hoverPrefabs;
        private PlayerHoverRaycastSystem _hoverNets;
        private readonly PlayerHoverShape[] _netHover = new PlayerHoverShape[PlayerHoverShape.MaxShapes];
        private int _netHoverCount;
        private PrefabBase _netHoverPrefab;
        private NetToolSystem.Mode _netHoverMode;

        private void CreateHoverCapture()
        {
            _hoverTools = World.GetOrCreateSystemManaged<ToolSystem>();
            _hoverRaycast = World.GetOrCreateSystemManaged<ToolRaycastSystem>();
            _hoverPrefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            _hoverNets = World.GetOrCreateSystemManaged<PlayerHoverRaycastSystem>();
        }

        private void ClearHoverCapture()
        {
            _netHoverCount = 0;
            _netHoverPrefab = null;
        }

        // Local definitions only: an isolated remote transaction can own previews too.
        public void ObserveHoverDefinitions(NativeArray<Entity> definitions)
        {
            if (_hoverTools.activeTool is not NetToolSystem tool || !InputManager.instance.controlOverWorld)
            {
                ClearHoverCapture();
                return;
            }
            if (!definitions.IsCreated || definitions.Length == 0) return;
            _netHoverCount = 0;
            _netHoverPrefab = tool.GetPrefab();
            _netHoverMode = tool.actualMode;
            Entity selected = _netHoverPrefab != null ? _hoverPrefabs.GetEntity(_netHoverPrefab) : Entity.Null;
            Entity underground = selected != Entity.Null && EntityManager.HasComponent<PlaceableNetData>(selected)
                ? EntityManager.GetComponentData<PlaceableNetData>(selected).m_UndergroundPrefab : Entity.Null;
            // Bounded; the display is deliberately partial for large grids and stamps.
            for (int i = 0; i < math.min(definitions.Length, 128) && _netHoverCount < _netHover.Length; i++)
            {
                Entity entity = definitions[i];
                if (!EntityManager.HasComponent<NetCourse>(entity) ||
                    !EntityManager.HasComponent<CreationDefinition>(entity) ||
                    EntityManager.HasComponent<OwnerDefinition>(entity)) continue;
                CreationDefinition definition = EntityManager.GetComponentData<CreationDefinition>(entity);
                if (definition.m_Prefab == Entity.Null ||
                    (definition.m_Prefab != selected && definition.m_Prefab != underground) ||
                    definition.m_Owner != Entity.Null ||
                    (definition.m_Flags & CreationFlags.Delete) != 0) continue;
                NetCourse course = EntityManager.GetComponentData<NetCourse>(entity);
                if (course.m_Length < 0.1f) continue;
                var shape = CurveShape(course.m_Curve, NetWidth(definition.m_Prefab), definition.m_Prefab.Index, true);
                if (ValidShape(shape)) _netHover[_netHoverCount++] = shape;
            }
        }

        private PlayerHoverShape[] CaptureHover()
        {
            using (Diagnostics.SyncProfiler.Measure("PartnerHover.Capture"))
            {
                ToolBaseSystem tool = _hoverTools.activeTool;
                if (tool == null || _camera == null || _camera.gamePlayController == null ||
                    !InputManager.instance.controlOverWorld || _hoverTools.fullUpdateRequired)
                {
                    ClearHoverCapture();
                    return Array.Empty<PlayerHoverShape>();
                }

                bool hasHit = _hoverRaycast.GetRaycastResult(out var hit);
                if (hasHit && tool.brushing)
                {
                    ClearHoverCapture();
                    return Single(Circle(hit.m_Hit.m_HitPosition,
                        math.clamp(tool.brushSize, 1f, 5000f), tool.GetHashCode(), true));
                }

                if (hasHit && tool is ObjectToolSystem objectTool && tool.GetPrefab() != null)
                {
                    ClearHoverCapture();
                    NativeList<ControlPoint> points = objectTool.GetControlPoints(out JobHandle dependencies);
                    dependencies.Complete();
                    if (points.IsCreated && points.Length != 0)
                    {
                        ControlPoint point = points[0];
                        Entity prefab = _hoverPrefabs.GetEntity(tool.GetPrefab());
                        if (TryBox(prefab, point.m_Position, point.m_Rotation, prefab.Index, true, out var box))
                            return Single(box);
                        return Array.Empty<PlayerHoverShape>();
                    }
                    return Array.Empty<PlayerHoverShape>();
                }

                if (tool is NetToolSystem netTool)
                {
                    NativeList<ControlPoint> points = netTool.GetControlPoints(out JobHandle dependencies);
                    dependencies.Complete();
                    // A cancelled course returns to the first point; drop its old curve.
                    if (!points.IsCreated || points.Length < 2 ||
                        netTool.GetPrefab() != _netHoverPrefab || netTool.actualMode != _netHoverMode)
                        _netHoverCount = 0;
                    if (_netHoverCount != 0)
                    {
                        var shapes = new PlayerHoverShape[_netHoverCount];
                        Array.Copy(_netHover, shapes, shapes.Length);
                        return shapes;
                    }
                    // A net tool without a course has only its cursor/snap point to show.
                    return Array.Empty<PlayerHoverShape>();
                }
                else ClearHoverCapture();

                // The tool's target, else the net under the cursor (no-tool raycasts skip nets).
                if (TryTargetShape(hasHit ? hit.m_Owner : Entity.Null, out PlayerHoverShape shape) ||
                    TryTargetShape(_hoverNets.NetHit, out shape)) return Single(shape);
                return Array.Empty<PlayerHoverShape>();
            }
        }

        /// <summary>The outline of a city entity from the sender's components; the receiver matches geometry.</summary>
        private bool TryTargetShape(Entity target, out PlayerHoverShape shape)
        {
            shape = default;
            if (target == Entity.Null || !EntityManager.Exists(target) ||
                EntityManager.HasComponent<Deleted>(target)) return false;

            Entity prefab = EntityManager.HasComponent<PrefabRef>(target)
                ? EntityManager.GetComponentData<PrefabRef>(target).m_Prefab : Entity.Null;

            if (EntityManager.HasComponent<Curve>(target))
            {
                shape = CurveShape(EntityManager.GetComponentData<Curve>(target).m_Bezier,
                    NetWidth(prefab), target.Index, false);
                return ValidShape(shape);
            }
            if (EntityManager.HasComponent<ObjectTransform>(target) && prefab != Entity.Null)
            {
                ObjectTransform transform = EntityManager.GetComponentData<ObjectTransform>(target);
                return TryBox(prefab, transform.m_Position, transform.m_Rotation, target.Index, false,
                    out shape);
            }
            if (EntityManager.HasComponent<global::Game.Net.Node>(target))
            {
                float3 position = EntityManager.GetComponentData<global::Game.Net.Node>(target).m_Position;
                shape = Circle(position, math.max(NetWidth(prefab), 8f), target.Index, false);
                return ValidShape(shape);
            }
            return false;
        }

        private bool TryBox(Entity prefab, float3 position, quaternion rotation, int key, bool placement,
            out PlayerHoverShape shape)
        {
            shape = default;
            if (prefab == Entity.Null || !EntityManager.HasComponent<ObjectGeometryData>(prefab)) return false;
            ObjectGeometryData geometry = EntityManager.GetComponentData<ObjectGeometryData>(prefab);
            Bounds3 bounds = geometry.m_Bounds;
            float height = math.clamp(bounds.max.y - bounds.min.y, 0f, 5000f);
            float3 baseCentre = position + new float3(0f, bounds.min.y, 0f);

            // A round tower has no corners to trace: a box around it stands well clear of the wall.
            if ((geometry.m_Flags & global::Game.Objects.GeometryFlags.Circular) != 0)
            {
                shape = Circle(baseCentre,
                    math.max(bounds.max.x - bounds.min.x, bounds.max.z - bounds.min.z), key, placement);
                return ValidShape(shape);
            }

            Quad3 corners = global::Game.Objects.ObjectUtils.CalculateBaseCorners(baseCentre, rotation, bounds);
            shape = new PlayerHoverShape
            {
                Kind = PlayerHoverKind.Box, Key = key, Placement = placement,
                A = Point(corners.a), B = Point(corners.b), C = Point(corners.c), D = Point(corners.d),
                Height = height
            };
            return ValidShape(shape);
        }

        private float NetWidth(Entity prefab) => prefab != Entity.Null &&
            EntityManager.HasComponent<NetGeometryData>(prefab)
                ? math.clamp(EntityManager.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth, 1f, 5000f)
                : 6f;

        private PlayerHoverShape CurveShape(Bezier4x3 curve, float width, int key, bool placement) =>
            new PlayerHoverShape
            {
                Kind = PlayerHoverKind.Curve, Key = key, Placement = placement, Width = width,
                A = Point(curve.a), B = Point(curve.b),
                C = Point(curve.c), D = Point(curve.d)
            };

        private PlayerHoverShape Circle(float3 point, float diameter, int key, bool placement) =>
            new PlayerHoverShape { Kind = PlayerHoverKind.Circle, A = Point(point),
                Width = math.clamp(diameter, 1f, 5000f), Key = key, Placement = placement };

        private static HoverPoint Point(float3 point) => new HoverPoint(point.x, point.y, point.z);
        private static bool ValidShape(PlayerHoverShape shape)
        {
            try { shape.Validate(); return true; }
            catch (ProtocolException) { return false; }
        }
        private static PlayerHoverShape[] Single(PlayerHoverShape shape) =>
            ValidShape(shape) ? new[] { shape } : Array.Empty<PlayerHoverShape>();
    }
}
