---
title: Changelog
---

# Changelog

## 0.1.6

This update focuses on synchronization, chat and general stability.

### Synchronization

- Renamed streets, buildings, districts and other entities now sync.
- Normally spawned buildings sync, including households, rent, income and more.

### Bug fixes

- The same error message could appear twice, even after being closed.
- Several chat layout issues.
- Redundant messages in certain situations.
- Multiple unnecessary resync triggers.
- A session could not continue because of a milestone screen.
- Client saves ignored the chosen difficulty level.

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
