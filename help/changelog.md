---
title: Changelog
description: "What changed in each release of the mod: new features, sync work and fixes."
---

# Changelog

## Version 1.7.1 - Working on

This update is mostly about performance in large cities. Hosts and clients both do far less work keeping a big city in sync, the host no longer stutters once a second, and painting trees no longer slows down the other players. It also adds Move It and Node Controller support, makes fires and traffic accidents the same for every player, adds IPv6 and more host controls, and fixes players being disconnected on slower Steam Relay connections.

### Performance

* Clients in large cities do much less work keeping homes and households in sync. Households that have not changed are skipped, families waiting for a free home are no longer re-applied again and again, and the checking work is limited by the number of residents, so very dense cities no longer slow it down.
* Workplaces use much less bandwidth: a company's employee list is only sent again when it actually changes.
* The host no longer stutters once a second in large cities. It used to collect all city data in a single frame every second; this is now spread across the whole second.
* The host does less work tracking households that leave the city, and no longer checks trees when looking for building colour changes.
* Painting trees and props with the object brush no longer slows down the other players. Strokes are sent as the finished objects and placed in one frame, instead of every brushed frame making the other players' games search the whole city. Dense strokes are sent in batches instead of one message per object.
* Steam Relay copes better with slow connections. It no longer speeds up blindly at the start of a world download, and outdated connection reports no longer keep cutting the transfer speed.
* The household money corrections do less work, and the host checks fewer homes per update.

### New

* Move It: moving buildings, props and trees is now synchronized once you let go. Objects with attached sub-objects are not supported yet.
* Move It and Node Controller road edits are now synchronized when the edit ends. If the mod cannot tell which road was edited, it holds the edit instead of changing the wrong road.
* Fires are now decided by the host: which buildings and trees catch fire (including from lightning), how bad the fire gets and whether a building burns down are the same for every player. The flames and fire engines still run on every computer.
* Traffic accidents now happen on the same road for every player. The cars involved can still differ.
* Data that compatible mods save with the city is now synchronized.
* Host Game now includes Approve Players and Simulation Sync.
* New Auto-Approve Steam Friends option: Steam friends can join right away while everyone else waits for approval. Steam Relay only.
* New setting for who may request a world sync: Allow, Ask Host or Host Only. With Ask Host, the host gets a popup showing who asked and why.
* Direct Connection now supports IPv6, for players whose internet provider does not give them their own IPv4 address. Host Address accepts IPv6, also with a port, like `[2001:db8::1]:25001`.
* When a player joins, the host compares both players' active mods and their versions and names exactly which ones differ. Ignore Mod Compatibility Checks (Own Risk) still lets them in.
* Save Diagnostics in the General options tab and `/diag` in the chat save one file with everything needed for a bug report into the game's Logs folder.
* Transport passenger and cargo statistics now show the host's numbers for every player.
* Clients now get the normal milestone popup when the host reaches a new milestone.

### Connection fixes

* Fixed players being disconnected on Steam Relay during a world download or in busy sessions. The client used to give up after 10 seconds; it now waits up to 60 seconds as long as data is still arriving.
* Fixed players being kicked with "rate limit exceeded: commands/sec" when the host's game stuttered.
* Losing the connection now says "Lost the connection to the host" instead of claiming the host disconnected you.
* Hosting a Direct Connection over the internet now requires a password of at least 8 characters, because anyone who found the port could otherwise join and download the city. Steam Relay and LAN Only are not affected.
* The Direct Connection host screen in the main menu now has Port, Password and LAN Only fields, and both host screens show how many password characters are still missing.

### Sync fixes

* Fixed fires appearing in different places for each player, and buildings burning down for one player while they still stood for the host.
* Fixed terraforming giving different heights for players running at different frame rates.
* Fixed roads built on freshly terraformed ground turning into a different road for the other player, for example a retaining-wall road for one and a ground road for the other.
* Straight roads now keep the exact slope they were built with for every player, even if the ground changed before the road arrived.
* Drawing a long road no longer triggers a world sync when the other player's game is a little behind.
* Statistics such as deaths, births and mail no longer slowly drift above the host's numbers.
* Zone-grown buildings now look the same for every player.
* Resetting a building's colour or a Road Speed Adjuster speed back to default is now synchronized.
* Household income is corrected right after the game recalculates it, so clients no longer show outdated values.
* Brush outlines for trees and props are no longer mistaken for terrain edits.
* Mod data is only applied when exactly one road, node or object matches, and a different mod version can no longer write values into the wrong place.
* Fixed the main-menu button, in-game button and milestone countdown all missing on some installations.

### Quality of life

* Milestone and unlock popups now close by themselves after 10 seconds in multiplayer, with a countdown bar, so an AFK player can no longer keep the whole session paused.
* The Options page shows the mod version, including the build, at the top of the General tab. The read-only status rows were removed, since the in-game multiplayer panel shows the same information live.
* The host's log records the mod build of every player who joins.

### Compatibility

* This update changes the network protocol, so all players must update together. Older versions cannot join.

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
