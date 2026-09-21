---
title: Multiplayer regression playset
description: "A clean, repeatable host/client setup for reproducing synchronization bugs."
---

# Multiplayer regression playset

Use this playset before reporting or reproducing a synchronization problem. It removes
third-party assets, mods and old save state as variables, so a result identifies the
multiplayer build rather than a local setup difference.

## Prepare both computers

1. Update Cities: Skylines II and CS2 Multiplayer Mod to the same build.
2. Create a new Paradox Mods playset named `CS2MP Regression`.
3. Add **only** CS2 Multiplayer Mod. Do not enable maps, assets, libraries or UI mods.
4. Enable the same gameplay DLC set on both computers. Radio stations and CS1 Treasure Hunt
   do not affect this test.
5. Restart the game after switching playsets, then record the `version@commit` value from the
   mod's status panel in the report.

## Use a disposable city

The host creates a new vanilla city on a standard map, saves it as
`CS2MP-regression-YYYY-MM-DD`, then hosts with Steam Relay where possible. The client joins,
waits for the initial world sync to complete, and does not build while the world is loading.

Do not use a production save for this test. Make a copy before testing an existing city.

## Regression sequence

Run one action at a time and wait until all players can see the result before the next one:

1. Draw a straight road, a curved road and a T-junction.
2. Upgrade and then bulldoze one road segment.
3. Place a quay against water and extend it with a second segment.
4. Place a small roundabout onto an existing road connection.
5. Place a building snapped to the road, move it, then add and remove an upgrade.
6. Have the client leave and rejoin while the host city remains open.
7. Run `/sync`, then repeat the quay and roundabout cases.

For each failed step, collect the host and affected-client logs before retrying. Include the
step number, who performed it, the `version@commit` identity, the active DLC list and whether
the host or client failed to see the result. The automatic diagnostic bundle contains the
operation and peer state needed to continue investigation.
