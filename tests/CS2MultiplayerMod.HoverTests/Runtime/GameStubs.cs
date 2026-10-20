// In-memory ECS/tool/render adapters. The production capture and renderer are linked
// unchanged; the installed game supplies its mathematical value types only.
using Colossal.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using Unity.Entities;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Unity.Jobs { public struct JobHandle { public void Complete() { } } }
namespace Unity.Collections
{
    public enum Allocator { Temp, Persistent }
    public struct NativeArray<T>
    {
        public T[] Items;
        public NativeArray(params T[] items) { Items = items; }
        public bool IsCreated => Items != null;
        public int Length => Items?.Length ?? 0;
        public T this[int i] => Items[i];
    }
    public struct NativeList<T>
    {
        public List<T> Items;
        public NativeList(int capacity, Allocator allocator) { Items = new(capacity); }
        public bool IsCreated => Items != null;
        public int Length => Items?.Count ?? 0;
        public T this[int i] => Items[i];
        public void Add(T item) => Items.Add(item);
        public void Clear() => Items.Clear();
        public void Dispose() { }
    }
}
namespace Unity.Entities
{
    public readonly record struct Entity(int Index) { public static Entity Null => default; }
    public class EntityManager
    {
        readonly Dictionary<Entity, Dictionary<Type, object>> entities = new();
        public Entity Create(params object[] components)
        {
            var e = new Entity(entities.Count + 1);
            entities[e] = components.ToDictionary(c => c.GetType());
            return e;
        }
        public bool Exists(Entity e) => entities.ContainsKey(e);
        public bool HasComponent<T>(Entity e) => Exists(e) && entities[e].ContainsKey(typeof(T));
        public T GetComponentData<T>(Entity e) => (T)entities[e][typeof(T)];
        public void SetComponentData<T>(Entity e, T data) => entities[e][typeof(T)] = data;
        public void AddComponent<T>(Entity e) where T : new() => entities[e][typeof(T)] = new T();
        public void RemoveComponent<T>(Entity e) => entities[e].Remove(typeof(T));
        public bool HasBuffer<T>(Entity e) => HasComponent<DynamicBuffer<T>>(e);
        public DynamicBuffer<T> GetBuffer<T>(Entity e, bool readOnly) => GetComponentData<DynamicBuffer<T>>(e);
    }
    public class DynamicBuffer<T> : List<T> { public int Length => Count; }
    public class World
    {
        readonly Dictionary<Type, object> systems = new();
        public T GetOrCreateSystemManaged<T>() where T : new()
        {
            if (!systems.ContainsKey(typeof(T))) systems[typeof(T)] = new T();
            return (T)systems[typeof(T)];
        }
    }
}
namespace Game
{
    public class GameSystemBase
    {
        public EntityManager EntityManager = new();
        public World World = new();
        protected virtual void OnCreate() { }
        protected virtual void OnDestroy() { }
        protected virtual void OnUpdate() { }
        public void Create() => OnCreate();
        public void Tick() => OnUpdate();
    }
}
namespace Game.Common { public struct Deleted { } }
namespace Game.Input { public class InputManager { public static InputManager instance = new(); public bool controlOverWorld = true; } }
namespace Game.Tools
{
    public class ToolBaseSystem
    {
        public bool brushing;
        public float brushSize;
        public Game.Prefabs.PrefabBase Prefab;
        public Game.Prefabs.PrefabBase GetPrefab() => Prefab;
    }
    public class ToolSystem { public ToolBaseSystem activeTool; public bool fullUpdateRequired; }
    public struct ControlPoint { public float3 m_Position; public quaternion m_Rotation; }
    public class NetToolSystem : ToolBaseSystem
    {
        public enum Mode { Straight, Curve, Replace }
        public Mode actualMode;
        public NativeList<ControlPoint> Points = new(4, Allocator.Persistent);
        public NativeList<ControlPoint> GetControlPoints(out JobHandle deps) { deps = default; return Points; }
    }
    public class ObjectToolSystem : ToolBaseSystem
    {
        public NativeList<ControlPoint> Points = new(4, Allocator.Persistent);
        public NativeList<ControlPoint> GetControlPoints(out JobHandle deps) { deps = default; return Points; }
    }
    public struct Hit { public float3 m_HitPosition; }
    public struct RaycastResult { public Entity m_Owner; public Hit m_Hit; }
    public class ToolRaycastSystem
    {
        public bool HasHit;
        public RaycastResult Hit;
        public bool GetRaycastResult(out RaycastResult hit) { hit = Hit; return HasHit; }
    }
    public struct Temp { }
    public struct OwnerDefinition { }
    [Flags] public enum CreationFlags { Delete = 1 }
    public struct CreationDefinition { public Entity m_Prefab, m_Owner; public CreationFlags m_Flags; }
    public struct NetCourse { public float m_Length; public Bezier4x3 m_Curve; }
}
namespace Game.Prefabs
{
    public class PrefabBase { public Entity Entity; }
    public class PrefabSystem { public Entity GetEntity(PrefabBase prefab) => prefab.Entity; }
    public struct PrefabRef { public Entity m_Prefab; }
    public struct PlaceableNetData { public Entity m_UndergroundPrefab; }
    public struct NetGeometryData { public float m_DefaultWidth; }
    public struct ObjectGeometryData { public Bounds3 m_Bounds; public Game.Objects.GeometryFlags m_Flags; }
}
namespace Game.Objects
{
    [Flags] public enum GeometryFlags { Circular = 1 }
    public struct Transform { public float3 m_Position; public quaternion m_Rotation; }
    public static class ObjectUtils
    {
        public static Quad3 CalculateBaseCorners(float3 p, quaternion r, Bounds3 b) => new(
            p + math.rotate(r, new float3(b.min.x, 0, b.min.z)),
            p + math.rotate(r, new float3(b.max.x, 0, b.min.z)),
            p + math.rotate(r, new float3(b.max.x, 0, b.max.z)),
            p + math.rotate(r, new float3(b.min.x, 0, b.max.z)));
    }
}
namespace Game.Net
{
    public struct Curve { public Bezier4x3 m_Bezier; }
    public struct Node { public float3 m_Position; }
}
namespace Game.Simulation
{
    public struct TerrainHeightData { }
    public class TerrainSystem { }
    public static class TerrainUtils
    {
        public static Func<float3, float> Height = _ => 0;
        public static float SampleHeight(ref TerrainHeightData data, float3 p) => Height(p);
    }
}
namespace UnityEngine
{
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }
}
namespace Game.Rendering
{
    public class CameraUpdateSystem { public object gamePlayController = new(); }
    public class OverlayRenderSystem
    {
        [Flags] public enum StyleFlags { Projected = 2 }
        public record Draw(StyleFlags Style, Line3.Segment Line, float Width);
        public struct Buffer
        {
            public List<Draw> Draws;
            public void DrawCircle(UnityEngine.Color outline, UnityEngine.Color fill, float line, StyleFlags style,
                float2 direction, float3 p, float width) => Draws.Add(new(style, new(p, p), width));
            public void DrawLine(UnityEngine.Color color, Line3.Segment line, float width, bool cameraFacing) => Draws.Add(new(0, line, width));
            public void DrawLine(UnityEngine.Color outline, UnityEngine.Color fill, float outlineWidth, StyleFlags style,
                Line3.Segment line, float width, float2 roundness) => Draws.Add(new(style, line, width));
        }
    }
}
namespace CS2MultiplayerMod.Game.Diagnostics
{
    public static class SyncProfiler
    {
        public struct Scope : IDisposable { public void Dispose() { } }
        public static Scope Measure(string label) => new();
    }
}
namespace CS2MultiplayerMod.Game.Sync.Players
{
    public class RemotePlayer
    {
        public long LastUpdateMs;
        public PlayerHoverShape[] Hover;
    }
    public class PlayerHoverRaycastSystem { public Entity NetHit; }
    public partial class PlayerCursorSyncSystem : global::Game.GameSystemBase
    {
        private global::Game.Rendering.CameraUpdateSystem _camera = new();
        public void Setup() => CreateHoverCapture();
        public PlayerHoverShape[] Capture() => CaptureHover();
    }
    public partial class RemotePlayerMarkerSystem
    {
        private const float SnapDistance = 350f;
        private float3 _localEye = default;
        private sealed class Trail
        {
            public PlayerHoverShape[] Hover = new PlayerHoverShape[PlayerHoverShape.MaxShapes];
            public int HoverCount;
        }
        private bool SphereVisible(float3 p, float radius) => true;
        public List<global::Game.Rendering.OverlayRenderSystem.Draw> Render(PlayerHoverShape shape)
        {
            _hoverTerrain ??= new global::Game.Simulation.TerrainSystem();
            var result = new List<global::Game.Rendering.OverlayRenderSystem.Draw>();
            var buffer = new global::Game.Rendering.OverlayRenderSystem.Buffer { Draws = result };
            var trail = new Trail();
            trail.Hover[0] = shape; trail.HoverCount = 1;
            DrawHover(buffer, trail, new UnityEngine.Color(1, 1, 1), false);
            return result;
        }
        public PlayerHoverShape Ease(PlayerHoverShape previous, PlayerHoverShape next)
        {
            var trail = new Trail { HoverCount = 1 };
            trail.Hover[0] = previous;
            AdvanceHover(trail, new RemotePlayer { Hover = new[] { next } }, 0.01f, 0);
            return trail.Hover[0];
        }
        public int Advance(PlayerHoverShape[] shapes, long lastUpdate, long now)
        {
            var trail = new Trail();
            AdvanceHover(trail, new RemotePlayer { Hover = shapes, LastUpdateMs = lastUpdate }, 0.1f, now);
            return trail.HoverCount;
        }
    }
}
