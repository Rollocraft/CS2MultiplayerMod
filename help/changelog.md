---
title: Changelog
description: "What changed in each release of the mod: new features, sync work and fixes."
---

# Changelog

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
