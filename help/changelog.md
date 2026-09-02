---
title: Changelog
description: "What changed in each release of the mod: new features, sync work and fixes."
---

# Changelog

## Unreleased

Fewer world reloads, and a log that says why the ones that remain happened.

### Synchronization

- A road placement waiting for the road it connects to no longer stops every other player's
  edits behind it, and bulldozing no longer runs ahead of it. A bulldoze applied while a
  placement waited could delete the very road that placement was anchored to, which then
  looked like the two cities had diverged and triggered a full world reload.
- Road endpoints can now be matched when the ground under them sits at a different height on
  the two machines. Terrain and water routinely drift by more than the three metres the
  matcher allowed, and past that point an ordinary road drawn near drifted ground could only
  ever end in a world reload.
- A large road edit is given time to finish in proportion to its size, and any progress
  restarts its clock. A 311-piece replacement was being abandoned on the same three-second
  budget that comfortably finished the 55-piece edits around it.
- A bulldoze that never found anything to remove is now reported. It leaves a road or building
  standing here that the other player no longer has, and it used to be visible only with
  verbose logging switched on.
- Terraforming now lands in one frame instead of being spread over several. Roads, buildings,
  zoning, growables and routes all wait for terrain to catch up, so every extra frame spent
  applying a stroke was a frame in which nothing else in the session could be applied either.
- Terraforming that goes missing is now reported instead of being dropped in silence. A stroke
  that could not be sent, or that arrived naming a tool this game does not have, leaves the
  ground a different shape on the two machines - and the roads drawn near it afterwards then
  fail to connect. Every one of those paths used to be invisible.
- A terraforming sample that could not be applied no longer stops the whole session. It kept
  the terrain queue permanently non-empty, and that queue is what every other kind of edit
  waits behind, so one bad sample could leave a session unable to apply anything at all.
- Transport lines and zoned buildings no longer give up on work they were never allowed to
  attempt. Both wait a fixed time for something they depend on, and both are held back while
  terrain or the road pipeline catches up - so the wait ran out against the clock rather than
  against attempts, and asked for a world reload over a command that had never once been tried.
- A zoned building waiting to join its road network no longer counts down that wait while the
  road pipeline is the thing being held back.
- Every remaining reason the mod can reload a world now records what it actually saw, instead
  of a short phrase. A reason that says commands were lost, or that this city contradicts the
  other player's, reloads at once; one that only says a deadline passed has to survive a hold
  first, and a fault that clears during that hold is logged as a reload that did not happen.
- Buildings, decorations, policies, appearance changes and transport lines no longer count down
  their waiting time while the thing they are waiting for is being held back by the mod itself.
  A prop waiting for its road, a policy waiting for its building and a line waiting to be
  created could all run out of patience against the clock rather than against attempts.
- A transport line whose commit was lost during a world reload no longer asks for another world
  reload. The incoming world already replaces it, and asking again is how a session gets stuck
  in a loop of them.
- Fixed a client that could sit waiting for a world forever. If the host finished a world
  handover before this city had installed it, the client asked for a replacement - but its own
  request was discarded as "a reload is already running", because the state it had just entered
  looked like one. Nothing was coming, and only a manual sync recovered it.

### Bug fixes

- Automatic world reloads now have to be justified before they happen. A reload costs both
  players a save, a transfer and a load, and does not fix the edit that triggered it, so the
  mod now writes down what it found, holds the mutating parts of the road pipeline still,
  retries, and reloads only if the problem is still there. A problem that clears itself is
  logged as a reload that did not have to happen.
- The log now says why a world reload happened: which edit, which endpoint, what stands there
  instead, how long the mod waited and what it tried first. Previously every cause printed one
  short phrase and the world reloaded.
- The host's log now records the reason a client asked to be re-synced, so a player pressing
  the sync button reads differently from a client whose pipeline gave up on an edit.

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
