# Hover regression checks

Run `dotnet run --project CS2MultiplayerMod.HoverTests -c Release` from the repository root. This compiles the actual game-independent Core sources and opens three loopback sessions with and without TLS.

Checks cover shape round trips, maximum packet size, every truncated prefix, invalid kinds/counts/flags, nonfinite and oversized geometry, randomized payload mutations, host relay and identity stamping, explicit clear messages, send/relay backpressure, and world-sync suspension/resumption.

A normal one-shape presence packet is 96 bytes; an empty packet is 34 bytes; the eight-shape maximum is 530 bytes, excluding transport overhead. At 10 Hz this is 0.96 KB/s normally and 5.3 KB/s at the maximum per destination. Hover capture uses existing tool results, reads at most 128 existing local road definitions per batch, and keeps at most eight visible curves. Rendering uses the existing overlay pass, culls offscreen shapes, smooths placement movement, and expires hover after 1.5 seconds without presence updates.

The separate legacy `tools/CoreTests` harness currently fails to compile against unrelated removed APIs. This project does not depend on that harness.

Live game validation is still required:
- Two players hover the same building/road while keeping independent local selection.
- Move and rotate a building footprint; draw/cancel straight and curved roads; change road prefab/mode.
- Place water/sewage pipes, underground electricity cables, and road/rail tunnels; check both partners' above-ground and underground views, hills, and tunnel entrances.
- Two players hover different segments of the same street; check each outline stays on its own segment and carries that player's colour.
- Hover stacked roads/utilities and compound buildings; check the outline traces the intended target and not its neighbour.
- Move a terrain or vegetation brush while paused.
- Move the cursor over UI, switch tools, delete the hovered object, disconnect, and resync the world.
- Toggle Show Partner Markers and check large-city frame timings with overlays on/off.

A partner's hover is drawn entirely by the mod, in that player's colour: a road segment along its own course, a building as its footprint, a junction as a ring. The game's own `Highlighted` outline is not used - it is a zero-size tag lit by one global shader colour, so it can show what a partner points at but never which partner, and on top of the mod's outline it read as a second, differently coloured highlight on the same object. Placement previews remain display-only outlines, without native ghost meshes or validation/error colours. Underground net variants are accepted and buried preview sections use the game's terrain projection; elevated sections retain their height. Large grids show a bounded subset of road curves.

Run `dotnet run --project CS2MultiplayerMod.HoverTests/Runtime -c Release` for focused capture and render regressions. This links the production source against in-memory ECS, tool, and overlay adapters and the installed game's mathematical value types (`CSII_MANAGEDPATH`). It covers underground prefab selection, depth and width preservation, tunnel/bridge projection commands, cancellation, UI focus, empty-ground suppression, shape limits, footprint and junction tracing, and expiry. These checks verify emitted drawing commands, not the game's final pixels.
