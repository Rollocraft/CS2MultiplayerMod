---
title: Changelog
description: "What changed in each release of the mod: new features, sync work and fixes."
---

# Changelog

## Version 1.7.1 - Working on

This update expands the regular Host Game controls, improves synchronization for dense object-brush edits and compatible third-party mods, adds synchronization for Move It and Node Controller edits, strengthens residential economy correction, and prevents milestone popups from leaving an entire session paused when a player is away. It also fixes players being dropped from sessions on slower Steam Relay connections and makes terraforming, and roads built on terraformed ground, look the same for every player.

### New

* Added Approve Players and Simulation Sync to the normal Multiplayer > Host Game flow.
* Added an Auto-Approve Steam Friends rule. Authenticated Steam friends may join immediately while everyone else still waits for host approval. This option is available for Steam Relay sessions only.
* Added a client resync policy with three choices: Allow, Ask Host, and Host Only.
* Ask Host now displays an in-game prompt containing the requesting player and reason, with Accept and Decline actions.
* Added synchronization support for durable ECS component and buffer state used by compatible third-party mods.
* Moving buildings, props and trees with Move It is now synchronized. The move is sent once, when the drag ends. Objects that carry dependent sub-objects are not synchronized yet; the log names them.
* Road geometry edits made with Move It or Node Controller are now synchronized. The final shape is sent once the edit ends and is applied only to the exact road that was edited. If two roads match equally well, the edit is held instead of changing the wrong road.
* Fires now start, spread and go out the same way for every player. The host's game decides which buildings and trees catch fire, including fires started by lightning, and the other players' games follow it. The fire itself, its damage and the fire engines still run on every computer.
* Passenger counts per transport type (residents and tourists) and cargo counts per transport type in the statistics now show the host's numbers for every player.
* When a player joins, the host now compares both players' other active mods, including their versions. A differing set is refused with a message naming exactly which mods are missing or differ. The host's Ignore Mod Compatibility Checks (Own Risk) setting still admits it.
* Added Save Diagnostics to the General options tab and `/diag` to the multiplayer chat. Both write one file with the session state and the flight log into the game's Logs folder, ready to attach to a bug report.
* The version on the General options tab now includes the source commit, and the host's log records the build of every player who joins.

### Bug fixes

* Hosting over the internet with a Direct Connection now requires a server password of at least 8 characters, because anyone who found the port could otherwise join and download the city. Steam Relay and LAN Only sessions are unaffected.
* Fixed fires appearing in different places for each player: every game used to roll its own fires, so a burning building on the host did not burn for anyone else.
* Statistics counters such as deaths, births and mail no longer creep above the host's numbers on the other players' games over time.

* Object and vegetation brush display footprints are no longer mistaken for terrain edits, avoiding rejected commands while the actual objects synchronize separately.
* Household income is now corrected immediately after the local game recalculates it, preventing clients from consuming stale residential-economy values.
* Milestone and building-unlock popups now close automatically after 10 seconds during multiplayer.
* This prevents an AFK host or client from indefinitely blocking simulation continuation for everyone else.
* Clients now receive the native milestone popup for newly reached host milestones, including milestone 1, without replaying earlier milestones or duplicating development points.
* Release builds now always include and validate the multiplayer UI bundle, preventing the main-menu button, in-game button and milestone countdown from all being absent on affected installations.
* Fixed players being disconnected during a world download or in busy sessions on the Steam Relay. The client used to give up after 10 seconds without a complete message even while data was still arriving. It now waits up to 60 seconds as long as traffic keeps coming in.
* When a player's game loses contact with the host, it now reports "Lost the connection to the host" instead of wrongly saying the host disconnected them.
* Fixed players being kicked with "rate limit exceeded: commands/sec" when the host's own game hitched. Traffic is now measured by when it arrived, short bursts are allowed, and an unusually high command rate is written to the log instead of ending the session.
* Fixed terraforming producing different heights for players whose games run at different frame rates. A player at 93 FPS used to receive about 1.5 times the height change from a 60 FPS player's brush strokes. Replayed brush strokes now move the ground exactly as far as they did for the player who made them.
* Fixed roads built onto freshly terraformed ground turning into a different road for the other player, for example an elevated road on a retaining wall for one player and a ground road on raised terrain for the other.
* Straight road segments now keep the exact slope they were built with on every machine, even if the terrain under them changed before the road arrived. Long spans are now measured along their full length.
* Resetting a Road Speed Adjuster speed back to default now also resets it for the other players.
* Resetting a building's custom colour back to default is now synchronized.
* Zone-grown buildings now use the same building variant for every player, and a mismatched variant is repaired when the building is next synchronized.
* Mod state for compatible third-party mods is only applied when exactly one road, node or object matches. Ambiguous matches are held instead of guessed, and objects must lie within the matching distance.
* Mod data is now matched by field names as well as field types, so a differing mod version can no longer write values into the wrong fields.

### Performance

* Added bounded batching for object-brush placements and deletions. Dense tree and prop strokes now travel as frame batches instead of flooding the session with one command per object.
* Improved Steam Relay congestion control so stale quality or ping reports do not repeatedly reduce the transfer rate while current traffic is being acknowledged successfully.
* Steam Relay transfers no longer speed up blindly before the first connection quality report, which previously overloaded slow paths at the start of a world download.
* Clients no longer re-apply residential occupancy pages whose content has not changed.
* Residential economy and purchase corrections do less work per update, and the host inspects fewer residential properties per update.

### Quality of life

* Added a visible countdown bar before milestone and building-unlock popups close, allowing players to dismiss them normally during the grace period.
* The mod's Options page now shows its version at the top of the General tab.
* Removed the read-only status rows from that page. The in-game multiplayer panel and the join screen already report the same session state live.

### Compatibility

* This version requires protocol version 69. All players must update to version 1.7.1 before joining the same session.

## Version 0.1.7 - 2026-09-15

This update focuses on improving performance, giving players more control over simulation synchronization, reducing unnecessary resyncs, fixing several gameplay synchronization issues, and expanding mod compatibility. It also brings support up to game version 1.6.2f1.

### Performance & Simulation Synchronization

* Added a new option to disable detailed simulation synchronization.
* This is especially useful for large cities with 100k+ residents, where the additional simulation synchronization workload can become expensive.
* In testing, detailed simulation synchronization reduced client FPS by roughly 50% and host FPS by roughly 30%.
* Disabling detailed simulation synchronization returns performance close to normal gameplay levels.
* Added synchronization for player highlighting and border previews. Partner markers now show hovered-object outlines and simplified building, road and brush placement previews in each player's colour, controlled by the existing Show Partner Markers setting.
* Moved the "Mods check" disable option into the Advanced tab.

### Synchronization & Stability

* Used your feedback and reported cases to further reduce unnecessary resync triggers.
* Improved synchronization behavior in several situations where temporary inconsistencies previously caused avoidable resyncs.
* Fixed the automatic divergence detection incorrectly triggering a full mod resync in some cases.
* Fixed subway nodes sometimes failing to connect correctly between players.
* Fixed roads occasionally appearing as dead-end streets after some time despite being connected.
* Fixed trees causing a resync.

### Mod Support

Added multiplayer compatibility for the following mods:

* Traffic
* Road Speed Adjuster

### Bug Fixes

* Fixed an issue where the host could not repay loans.
* Fixed several synchronization-related edge cases reported by players.
* Improved visibility of UI checkboxes and status indicators.

### Compatibility

* Updated for Cities: Skylines II 1.6.2f1.
* This release changes the network protocol, so all players must update together. Older builds cannot join a 0.1.7 session.

## Version 0.1.6.1h1 - 2026-09-03

### Fixes

* The main-menu Multiplayer button could be missing on the launch that installs a mod update. Paradox Mods can finish enabling the mod tens of seconds after the main menu is already drawn, and the menu does not pick the button up on its own once it is on screen. The mod now notices that the button never reached the menu and rebuilds the menu interface, instead of needing a game restart.
* The log now records whether the button actually reached the menu, not just that the interface module registered.

## Version 0.1.6.1 - 2026-09-02

This update marks a major step forward for the project.

Until now, synchronization was primarily focused on keeping the world itself consistent between players: roads, player-placed buildings, networks, pipelines, electricity infrastructure and other physical changes to the city.

With **0.1.6.1, we have expanded the core synchronization system to include some parts of the simulation itself.**

This means the mod is no longer only synchronizing what players build. It now also synchronizes more of what the game simulation changes on its own.

### Simulation Synchronization

* Residential simulation synchronization has received major performance improvements.
* Naturally spawned Industrial, Commercial and Office buildings are now synchronized between host and clients.
* Population is now better synchronized between host and clients.
* City income and economic values are synchronized much more closely.
* Simulation-driven financial values such as taxes, fees and service costs are now included in synchronization.
* Improved synchronization of changes caused directly by the game simulation rather than player actions.

This brings multiplayer significantly closer to running the same city simulation on every client, instead of only maintaining the same physical city layout.

### Performance

The synchronization system itself has become significantly more efficient in this update.

However, 0.1.6.1 also massively increases the amount of data the mod has to process. Instead of only reacting to player actions and major world changes, the mod can now deal with thousands of simulation events every second.

Because of this, large and highly populated cities may still begin to experience noticeable slowdown or lag.

In other words: the synchronization code is faster than before, but it is also doing far more work than before. The increased simulation workload can currently outweigh those performance improvements in larger cities.

Improving performance under these new workloads will remain an important focus going forward.

### Synchronization & Stability

* Significantly reduced unnecessary world reloads by improving how synchronization failures are detected and verified before triggering a resync.
* World reloads now record detailed information about what caused them, making synchronization issues significantly easier to diagnose.
* Improved road synchronization when connected roads, terrain height differences or large road edits temporarily delay placement.
* Large road edits are now given more time to finish based on their size and progress instead of relying on a fixed timeout.
* Improved synchronization queue handling so buildings, policies, transport lines and other dependent changes no longer time out while waiting for another synchronization system to finish.
* Improved terraforming synchronization and processing speed.
* Terraforming changes are now applied in a single frame instead of being spread across multiple frames.
* Invalid or missing terrain updates can no longer silently block the entire synchronization pipeline.
* Improved handling of bulldoze operations that fail to find the expected object.
* Fixed several cases where delayed transport, zoning, road and building synchronization could incorrectly trigger world reloads.
* Fixed a case where transport line synchronization could cause repeated world reloads after an incoming world was already being applied.
* Fixed a case where clients could become stuck waiting indefinitely for a world resynchronization.
* Improved handling of special Industries that previously caused resyncs.

### Bug Fixes

* Fixed bridges breaking when placed at height level 0.
* Fixed several situations that could cause synchronization instability.
* Fixed special Industries triggering unnecessary resyncs.
* Automatic world reloads are now verified before being triggered, allowing temporary synchronization issues to recover without forcing a full reload.
* The host log now records why a client requested a resynchronization, making manual sync requests distinguishable from synchronization failures.
* Fixed that client could not see demand.

### Quality of Life

* Improved logging throughout the synchronization systems, making issues easier to identify, reproduce and fix.
* World reload logs now include more context about the affected edit, what was found instead, how long the system waited and what recovery steps were attempted first.

## Contributing

Contributing to the project has previously been difficult, if not nearly impossible.

Going forward, the project will have a dedicated development branch, making it significantly easier for other developers to contribute, test changes and help improve the mod.

## 0.1.6 - 2026-08-24

This update focuses on synchronization, chat and general stability.

### Synchronization

- Renamed streets, buildings, districts and other entities now sync.
- Normally spawned Residential buildings sync, including households, rent, income and more.
### Bug fixes

- The same error message could appear twice, even after being closed.
- Several chat layout issues.
- Redundant messages in certain situations.
- Multiple unnecessary resync triggers.

### Quality of life

- ESC closes all supported screens, and leaves the chat input.
- Steam players get their Steam name as the preset player name.
- A mod checker that can be disabled in the mod settings.
- Chat behaviour matches Steam Relay better.
- New languages: French, Spanish, Italian, Polish, Russian, Japanese, Simplified Chinese.
- Clearer errors and warnings, with references to the help pages.
- [This documentation site](index.md), covering setup, connections and every error the
  mod can show

## 0.1.5h2 - 2026-08-12

- Fixed the mod not loading for non-Steam players.

## 0.1.5h1 - 2026-08-11

- Fixed CS1 Treasure Hunt blocking a connection.
- Fixed client placement throwing an exception.

## 0.1.5 - 2026-08-11

Steam Relay arrived: no port forwarding and no manual IP setup. Host a game, share your
lobby code, and friends join instantly.

Steam Relay only works if every player owns the game on Steam. Xbox and unofficial versions
keep using a direct connection. If you know how to set up a direct connection, prefer it -
the relay syncs about twice as slowly.

The rest of the update focuses on server administration, building synchronization and
performance.

### New

- Synchronization for natural disasters.
- Synchronization for moved buildings.
- Synchronization for building upgrade removals.
- Synchronization for building policies.
- Synchronization for city policies.
- Hosts have to accept players before they can join.
- Players can be kicked.
- Players can be banned.
- A player list in the in-game UI.
- The Join Game menu became Multiplayer, making hosting and joining more intuitive.

### Bug fixes

- Synchronization for flight routes.
- Synchronization for hydroelectric dams.
- Synchronization for special industries, trash stations and other draggable-border
  buildings.
- Unnecessary resyncs caused by building upgrades.
- A resync triggered by placing roundabouts.
- Intersections sometimes not placing correctly.
- Roundabouts in intersections not being centered after slight intersection changes.
- A zoning bug that could instantly abandon buildings.
- A crash related to building streets.
- A crash caused by repeatedly recoloring buildings.
- Clients could not resume the game while the host was on the Milestone Reached screen.
- World resync snapshots caused autosave-pruning errors.

### Performance

- Fixed a freeze when adding an extension.
- Better performance in Building Mode.
- Better performance when zoning or dezoning large areas.

### Quality of life

- Better error messages for hosts.

## 0.1.4 - 2026-07-22

This update focuses on synchronization improvements, crash fixes and a smoother multiplayer
experience. There were also many smaller fixes for stability.

### Bug fixes

- Large intersections were placed incorrectly.
- Details were missing when placing streets.
- Train stations showed a cross on the railway when tracks were built from them.
- Public transportation did not sync correctly.
- Train stations did not properly integrate into streets.
- Buildings incorrectly displayed "Not connected" because of pathway issues.
- Special industries did not sync correctly and could cause crashes.
- The causes behind the submitted crash logs were identified and fixed.

### Quality of life

- A synchronization screen for the host when a player joins or resyncs, so both players can
  keep the same world state.
- The mod now tries to detect synchronization problems and resync automatically.
- Updated hosting interface.
- Clearer and easier to understand error messages when connecting.

## 0.1.3 - 2026-07-11

### Bug fixes

- Building placement issues.
- Streets not syncing.

### Performance

- Reduced network traffic.
- Optimized street placement.

### New

- A crash logging system.

## 0.1.2 - 2026-07-09

- Building and road spam no longer crashes the game.
- Roundabouts place correctly.
- Build mode synchronizes properly.

## 0.1.1 - 2026-07-08

- Host-authoritative sessions, world transfer on join, and synchronized roads, zoning,
  services, finances, progression, time and weather.

## 0.1.0

- The first public release.
