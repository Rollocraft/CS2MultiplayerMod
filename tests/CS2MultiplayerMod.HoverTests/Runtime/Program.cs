using Colossal.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Game;
using CS2MultiplayerMod.Game.Sync.Players;
using Game.Input;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}
Bezier4x3 Course(float y) => new(new(0, y, 0), new(10, y, 0), new(20, y, 0), new(30, y, 0));

var capture = new PlayerCursorSyncSystem();
capture.Setup();
var em = capture.EntityManager;
Entity underground = em.Create(new NetGeometryData { m_DefaultWidth = 2 });
Entity surface = em.Create(new NetGeometryData { m_DefaultWidth = 16 }, new PlaceableNetData { m_UndergroundPrefab = underground });
var net = new NetToolSystem { Prefab = new PrefabBase { Entity = surface } };
net.Points.Add(new()); net.Points.Add(new());
var tools = capture.World.GetOrCreateSystemManaged<ToolSystem>();
tools.activeTool = net;
Entity Definition(Entity prefab, float y, params object[] extra) => em.Create(new object[] {
    new CreationDefinition { m_Prefab = prefab }, new NetCourse { m_Length = 30, m_Curve = Course(y) }
}.Concat(extra).ToArray());
var pipe = Definition(underground, -20);
capture.ObserveHoverDefinitions(new NativeArray<Entity>(pipe));
var hover = capture.Capture();
Check(hover.Length == 1 && hover[0].Placement, "underground prefab is captured");
Check(hover[0].Width == 2 && hover[0].A.Y == -20 && hover[0].D.Y == -20, "pipe width and actual depth are preserved");
var pipeShape = hover[0];
capture.ObserveHoverDefinitions(default);
Check(capture.Capture().Length == 1, "stationary preview survives frames without regenerated definitions");
var renderer = new RemotePlayerMarkerSystem();
Check(renderer.Render(pipeShape).Count > 0 && renderer.Render(pipeShape).All(d => d.Style == OverlayRenderSystem.StyleFlags.Projected), "buried pipe uses native terrain projection");
var bridge = Definition(surface, 20);
capture.ObserveHoverDefinitions(new NativeArray<Entity>(bridge));
var bridgeShape = capture.Capture()[0];
Check(renderer.Render(bridgeShape).Count > 0 && renderer.Render(bridgeShape).All(d => d.Style == 0 && d.Line.a.y == 20 && d.Line.b.y == 20), "bridge stays at its actual elevation");
TerrainUtils.Height = p => p.x > 10 && p.x < 20 ? 40 : 0;
var mixed = renderer.Render(bridgeShape);
Check(mixed.Any(d => d.Style != 0) && mixed.Any(d => d.Style == 0), "tunnel through a hill projects only buried sections");
TerrainUtils.Height = _ => 0;
net.Points.Clear(); net.Points.Add(new());
Check(capture.Capture().Length == 0, "cancel clears course without cursor fallback");
net.Points.Add(new());
capture.ObserveHoverDefinitions(new NativeArray<Entity>(pipe));
net.actualMode = NetToolSystem.Mode.Curve;
Check(capture.Capture().Length == 0, "mode switch clears old course");
capture.ObserveHoverDefinitions(new NativeArray<Entity>(pipe));
InputManager.instance.controlOverWorld = false;
Check(capture.Capture().Length == 0, "UI focus clears preview");
InputManager.instance.controlOverWorld = true;
Check(capture.Capture().Length == 0, "returning from UI cannot resurrect old preview");
foreach (var invalid in new[] { Definition(underground, -20, new OwnerDefinition()), Definition(em.Create(new NetGeometryData()), -20) })
{
    capture.ObserveHoverDefinitions(new NativeArray<Entity>(invalid));
    Check(capture.Capture().Length == 0, "owned or unrelated network definitions do not leak into hover");
}
var deleted = Definition(underground, -20);
em.SetComponentData(deleted, new CreationDefinition { m_Prefab = underground, m_Flags = CreationFlags.Delete });
capture.ObserveHoverDefinitions(new NativeArray<Entity>(deleted));
Check(capture.Capture().Length == 0, "deletion definition is not a placement");
var tooMany = Enumerable.Range(0, 20).Select(_ => Definition(underground, -20)).ToArray();
capture.ObserveHoverDefinitions(new NativeArray<Entity>(tooMany));
Check(capture.Capture().Length == PlayerHoverShape.MaxShapes, "preview count remains bounded");
tools.activeTool = new ToolBaseSystem();
var raycast = capture.World.GetOrCreateSystemManaged<ToolRaycastSystem>();
raycast.HasHit = true;
raycast.Hit = new RaycastResult { m_Hit = new Hit { m_HitPosition = new float3(20, 0, 30) } };
Check(capture.Capture().Length == 0, "empty terrain never emits a cursor circle");
var footprint = new PlayerHoverShape { Kind = PlayerHoverKind.Box, Height = 30,
    A = new HoverPoint(0, 0, 0), B = new HoverPoint(10, 0, 0), C = new HoverPoint(10, 0, 8), D = new HoverPoint(0, 0, 8) };
Check(renderer.Render(footprint).Count == 4, "a hovered building is traced as its own footprint");
var junction = new PlayerHoverShape { Kind = PlayerHoverKind.Circle, Width = 5 };
Check(renderer.Render(junction).Count == 1, "a hovered junction is traced as one ring");
var hovered = pipeShape; hovered.Placement = false;
Check(renderer.Render(hovered).Count > 0, "a hovered road is traced along its own course");
var newCourse = pipeShape;
newCourse.A.X += 30; newCourse.B.X += 30; newCourse.C.X += 30; newCourse.D.X += 30;
Check(renderer.Ease(pipeShape, newCourse).A.X == newCourse.A.X, "new anchored road segment snaps instead of morphing from the last course");
Check(renderer.Advance(new[] { pipeShape }, 0, 1501) == 0, "stale hover expires");
Check(renderer.Advance(Array.Empty<PlayerHoverShape>(), 0, 0) == 0, "explicit clear hides hover");

// A hovered building travels as its own footprint, with no local entity to hand to the game.
Entity buildingPrefab = em.Create(new ObjectGeometryData { m_Bounds = new Bounds3(new(-5, 0, -8), new(5, 20, 8)) });
Entity building = em.Create(new PrefabRef { m_Prefab = buildingPrefab }, new Game.Objects.Transform { m_Rotation = quaternion.identity });
raycast.Hit = new RaycastResult { m_Owner = building };
var buildingShape = capture.Capture()[0];
Check(!buildingShape.Placement && buildingShape.Kind == PlayerHoverKind.Box, "a hovered building is captured as a footprint");
Check(buildingShape.Height == 20 && renderer.Render(buildingShape).Count == 4, "its height survives and all four sides are drawn");
Console.WriteLine($"Hover runtime regression checks passed: {checks}");
