Repo: https://github.com/Rollocraft/CS2MultiplayerMod

Newest Changelog and developments on Discord: https://discord.gg/KFZTW2YSJt

## V 0.1.5h2
Hotfix V0.1.5h2

- Fixed mod not loading for non steam players

Make sure you update to the latest version of the mod and have fun! 🎉

## V 0.1.5h1
Hotfix V0.1.5h1

- Fixed CS1TreasureHunt blocking a connection
- Fixed client placement throwing an exception

Every new version must pass the Paradox Mod Security Checking Pipeline before it becomes publicly available. As a result, the update may not be available immediately after release.

Make sure you update to the latest version of the mod and have fun! 🎉

## V 0.1.5
Version 0.1.5
We integrated Steam Relay technology, so you no longer need port forwarding or manual IP configuration to play with friends and reduce friction. Simply host a game, share your lobby code, and your friends can join instantly.

Important: Steam Relay only works if all players own the game on Steam. 

Xbox versions are not supported and must continue using the classic Direct Connect method.

Also if you have the technical know how to setup a direct connect, please refer to doing this as Steam Relay will have longer Sync timings (around 2 times more then direct connect).

This update also focuses on server administration, building synchronization, and overall performance improvements.

### New

#### Synchronization
- Added synchronization for natural disasters.
- Added synchronization for moved buildings.
- Added synchronization for building upgrade removals.
- Added synchronization for building policies.
- Added synchronization for city policies.
#### Multiplayer
- Added the requirement for hosts to accept players before they can join.
- Added the ability to kick players.
- Added the ability to ban players.

### Bug Fixes
#### Synchronization
- Fixed synchronization for flight routes.
- Fixed synchronization for hydroelectric dams.
- Fixed synchronization for special industries, trash stations, and other draggable-border buildings.
- Fixed unnecessary resyncs caused by building upgrades.
- Fixed  a resync getting triggered by placing Roundabouts
- Fixed upgrades not moving
#### Gameplay
- Fixed intersections sometimes not placing correctly.
- Fixed roundabouts in intersections not being centered after slight intersection changes.
- Fixed a zoning bug that could instantly abandon buildings.
- Fixed a crash related to building streets.
- Fixed a crash caused by repeatedly recoloring buildings.
#### Multiplayer
- Fixed clients being unable to resume the game while the host was on the Milestone Reached screen.
- Fixed multiplayer world resync snapshots causing autosave-pruning ArgumentNullException errors.
#### Performance
- Fixed a freeze when adding an extension.
- Improved performance in Building Mode.
- Improved performance when zoning or dezoning large areas.
#### Quality of Life
- Added better error messages for hosts.
- Added a player list to the in-game UI.
- Reworked the Join Game menu (now Multiplayer) to make hosting and joining worlds more intuitive.

Every new version must pass the Paradox Mod Security Checking Pipeline before it becomes publicly available. As a result, the update may not be available immediately after release.

Make sure you update to the latest version of the mod and have fun! 🎉

## V 0.1.4
Version 0.1.4 is now available!

Sorry for the delay, we got deep into the code and fixed a lot of issues and i was 1 week on vacation.

This update focuses on synchronization improvements, crash fixes, and a smoother multiplayer experience.

### Bug Fixes
- Fixed large intersections being placed incorrectly.
- Fixed missing details when placing streets.
- Fixed public transportation lines not syncing at all.
- Fixed bus stations not syncing correctly, making it impossible to create bus lines.
- Fixed buildings incorrectly displaying “Not connected” due to pathway issues after Upgrading them or adding Extensions.
- Fixed railways not correctly adding to Train stations, making it impossible to create train lines.
- Added a synchronization screen for the host when joining or resyncing, helping both players maintain the same world state.
- Reviewed the submitted crash logs, identified their causes, and implemented fixes.

### Quality-of-Life Improvements
- Updated the hosting interface.
- The mod now attempts to detect synchronization issues and automatically resync the game.
- Added clearer and easier-to-understand error messages when connecting.

Thank you for your reports on Discord and 2.000 playset downloads. 

Note: Each new version is undergoing Paradox Mod Security checking Pipeline so it could be that u can update not directly

Happy playing!🎉 

## V 0.1.3
V 0.1.3  is out!

- Fixed: Building placement issues
- Fixed: Street not syncing
- Optimized: Network traffic
- Optimized: Street Placement
- Added: New Crash logging system

Note: Each new version is undergoing Paradox Mod Security checking Pipeline so it could be that u can update not directly

Happy Playing  🎉 

## V 0.1.2
V. 0.1.2  is out!

We improved stability and fixed some bugs. Multiplayer is now more stable and less likely to crash. We also fixed the roundabout issue and improved sync in build mode. Thanks to all of you reporting Bugs and Crashes, this helps the development of the CS2 Multiplayer Mod!

Fixes:
- Street Spam not lead to crash
- Building spam not lead to crash
- Being in Building Mode leads to crash
- Roundabout sync finally work
- Sync not working/partly in build mode

Note: Each new version is undergoing Paradox Mod Security checking Pipeline so it could be that u can update not directly
Happy Playing  🎉 
