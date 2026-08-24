---
title: Errors and warnings
---

# Error and Warning Reference

This page lists the errors and warnings a player can encounter in CS2 Multiplayer Mod. It covers the messages shown in the multiplayer UI and the warning families written to the game log. Dynamic details such as addresses, player names, prefab names, ports, and exception text are shown in angle brackets.

Start with the exact headline shown in game. If the UI only says that multiplayer could not complete the connection, check the nearby detail and then see [Generic connection failure](#multiplayer-could-not-complete-the-connection).

## Connection and compatibility errors

### The password was not accepted

Possible internal details:

- `This server requires a password.`
- `Incorrect password.`

The joining player must enter the host's password exactly; it is case-sensitive. Repeated failures can temporarily block the joining address. Confirm the password privately with the host and retry.

### Your multiplayer mod versions do not match

Possible internal details:

- `Protocol mismatch: host v<version>, this build v<version>.`
- `Mod version mismatch: host <version>, client <version>.`

Update CS2 Multiplayer Mod on both computers and restart both games. A host can enable Ignore Mod Compatibility Checks (Own Risk) to admit a different multiplayer-mod build when both builds still use the same network protocol. A protocol mismatch cannot be ignored because the builds may encode network data differently.

See [Mod Version Issues](troubleshooting.md#mod-version-issues).

### Your Cities: Skylines II versions do not match

Possible internal detail: `Game version mismatch: host <version>, client <version>.`

Both players must update the game, use the same Steam branch, and restart. The own-risk mod setting does not bypass this check.

See [Game Version Issues](troubleshooting.md#game-version-issues).

### The enabled DLCs do not match

The detailed message names DLC the joining player or host is missing. Both machines need the same sync-relevant DLC set. The own-risk mod setting does not bypass this check.

See [Disabling DLC](disable_dlc.md).

### Other mods are enabled

The detail lists the other active mods that were detected. By default, hosting and joining are blocked because additional mods can change simulation behavior, tools, or prefab catalogs.

Disable every other mod in the active playset and restart if the message says those mods are still loaded. Advanced users can enable Ignore Mod Compatibility Checks (Own Risk) while offline, but that can cause desyncs, broken cities, or crashes.

See [Mod Support and Compatibility](mods.md).

### This multiplayer session is full

Possible internal detail: `Server is full (<count> players).`

Wait for another player to leave or ask the host to increase Max Players before starting the next session. A join that waited for approval can also receive this error if the final seat was taken before the host clicked Accept.

### The host address could not be found

Possible internal details include `HostNotFound`, `NoData`, `could not be resolved`, or a DNS lookup failure.

Check for typing mistakes. For a direct connection on the same network, use the host computer's local IP address. For internet play, use the host's public address. Steam Relay uses a 17-digit join code instead of an address.

See [Connection Issues](troubleshooting.md#connection-issues).

### The host answered, but no session is listening on that port

Possible internal detail: `ConnectionRefused`.

Make sure the host has started the session and both players entered the same port. Also check whether a firewall or security product is actively rejecting the connection.

See [Diagnose issues with port forwarding](forwarding_troubleshoot.md).

### The host did not answer in time

Possible internal details include `TimedOut`, `timed out`, or a handshake timeout.

Verify that the host is still online and that the address and port are correct. Allow the port through the host firewall. Direct internet sessions also need a TCP port forward. If the join was waiting for approval, the host must respond before the approval timeout.

See [Diagnose issues with port forwarding](forwarding_troubleshoot.md).

### This PC cannot reach the host's network

Possible internal details: `NetworkUnreachable` or `HostUnreachable`.

Check both players' internet or local-network connections. On the same network, use the host's local IP. Across networks, use Steam Relay or a correctly forwarded direct connection.

See [Diagnose issues with port forwarding](forwarding_troubleshoot.md).

### The hosting port is already in use

Possible internal detail: `AddressAlreadyInUse`.

Close the other game/server process using the port, or choose a different port. Every direct-connection player must use the new port, and the router/firewall rule must match it.

See [Diagnose issues with port forwarding](forwarding_troubleshoot.md).

### The host removed you

Possible internal details mention that the host removed, kicked, or banned the player.

A kick ends the current connection. A session ban also prevents that network address from rejoining until the host ends the session. Contact the host before trying again.

### The host did not let you in

Possible internal details:

- `The host declined your request to join.`
- `The host did not respond to your join request in time.`

When Approve Joining Players is enabled, the host must accept the request. Ask the host to keep the multiplayer panel open and retry.

### Steam Relay is unavailable or the join code is invalid

Possible internal details:

- `Cannot host over the Steam relay: <reason>`
- `Cannot join over the Steam relay: <reason>`
- `Enter the host's join code first.`
- `'<code>' is not a valid join code.`
- `Failed to host over the Steam relay: <reason>`

Launch the Steam copy of the game through Steam and confirm Steam is online. A join code has 17 digits. If Relay remains unavailable, both players can select Direct Connection and use an address and port instead.

### Multiplayer could not complete the connection

This is the fallback for a connection fault that has no more specific friendly category. The detail shown beneath it and the `Session fault:` line in the log contain the original reason.

Retry once. If it repeats, restart both games, verify the target, update the mod, and check [Troubleshooting](troubleshooting.md). For a reproducible problem, include the logs described under [Reporting a problem](#reporting-a-problem).

## Session and world errors

### Could not close the shared city

The game rejected all automatic attempts to return a disconnected client to the main menu. The mod deliberately keeps the temporary host-world copy while it is open instead of deleting data underneath the loaded world.

Click Try Again. If it continues to fail, close the game rather than continuing to edit the disconnected temporary copy. Restarting lets the mod clean up safely.

### Host world did not start loading

Recognizable log text:

- `Host world never started loading.`
- `Could not auto-load the host world.`
- `Failed to stage host map.`
- `Host world staged but could not be registered with the save index.`

The connection can remain alive while world loading fails. Ask the host to use Sync World or use `/sync` in chat. If it repeats, verify game files and check free disk space and write access to the Cities: Skylines II saves folder.

### World transfer stopped or was rejected

Recognizable log text includes `Abandoning stalled blob`, `Dropping blob`, `Replacing incomplete blob`, `Ignoring map transfer`, or `world-sync epoch ... aborted`.

The mod rejects malformed, oversized, stale, or stalled transfers to protect the session. Retry Sync World. Repeated failures usually indicate an unstable connection, incompatible content, or a mod defect.

### World copy errors

The Save Copy dialog can report:

- A saved world with this name already exists — choose another name.
- Enter a name between 1 and 85 characters — shorten or correct the name.
- Wait until the host world has fully loaded — retry after joining finishes.
- The copy could not be saved — try another name, check free disk space, and verify write access.
- `Could not capture a preview` in the log — the save can still succeed, but its thumbnail may be absent.

### Autosave could not be paused or restored

Clients normally pause autosave while using the host's temporary world and restore it on disconnect. If the log says autosave could not be restored, re-enable autosave in the normal game options after leaving multiplayer.

## Warning banners and configuration warnings

### Untested game version

The installed game build is not in the mod's tested-version list. Multiplayer may still work, but a game update can change simulation or UI behavior. Keep backups and check for a mod update.

### Other mods enabled

In normal mode this warning blocks Host and Join. Disable the listed mods in the active playset. A warning sourced from `loaded assemblies (restart to clear)` requires a game restart after disabling them.

### Compatibility check ignored

Ignore Mod Compatibility Checks (Own Risk) is enabled while other mods are active, or a host admitted a different CS2 Multiplayer Mod build. This is advisory but serious: desyncs, missing prefabs, broken saves, and crashes are possible. The network protocol, game version, and DLC set are still checked.

### Invalid port, player limit, or re-sync interval

Recognizable log text:

- `Invalid host port` or `Invalid join port` — the default port `25001` is used.
- `Invalid max players` — the default of `8` is used; valid values are `2` through `32`.
- `World re-sync interval ... is not a whole number` — the safe default is used.

Correct the value in Options before the next session.

### Public hosting with no password

Recognizable log text begins `[security] Hosting PUBLICLY with NO PASSWORD` or `PUBLIC HOSTING ENABLED`.

A direct public host accepts internet connections to the forwarded port, and joined players receive the city. Set a strong password, keep it private, or use Steam Relay/LAN-only mode.

### Automatic port forwarding failed

Recognizable log lines begin `[upnp]` and may say no local address was found, the router refused the request, verification failed, or automatic forwarding failed.

Hosting is still active locally. Configure the TCP port manually or use Steam Relay. See [Port Forwarding](forwarding.md) and [Port Forwarding Troubleshooting](forwarding_troubleshoot.md).

### Multiplayer UI did not load

Recognizable messages include:

- `The multiplayer UI module never reported in`
- `Multiplayer screen unavailable`
- `GameBottomRight append failed`
- `menu connection view could not be registered`
- `in-game connection view could not be registered`

Another UI mod can stop the game's UI-module chain, or the multiplayer `.mjs` file may be missing. Remove or update the UI mod named near the first JavaScript exception. Joining remains available through Options > CS2 Multiplayer Mod > Join Game when the generated options UI is working.

### Steam Relay transport warnings

Messages about pre-warming, relay send/receive failures, refused relay settings, queued bytes at shutdown, or dropped relay connections indicate Steam networking trouble. Restart Steam and the game, confirm Steam is online, then retry. Switch both players to Direct Connection if Relay remains unavailable.

### A session or host-control action was ignored

Messages such as `Cannot host`, `Cannot join`, `Cannot choose a host world`, or `Ignored approve/decline/kick/ban request` mean the requested action was no longer valid when it reached the game. Common reasons are that the mod was disabled, another session was already active, or the player/pending request disappeared while the UI was updating. Check the current session state and try again; this is usually harmless if it occurs once.

### Direct transport policy or queue warning

Recognizable text includes `session is LAN-only`, `too many open connections`, `Transport event queue full`, `Peer timed out`, `Handshake timed out`, or `connection(s) still draining`.

- A LAN-only refusal is expected when an internet address reaches a host that deliberately accepts only private-network clients.
- Too many connections can be a burst of pending joins or unwanted traffic. Keep a public host password-protected.
- A full transport queue or timeout means the game/network could not process traffic fast enough. Stop the session if it repeats, reduce load, and retry.
- Connections still draining during a deliberate shutdown are forcibly closed after the farewell timeout; no manual cleanup is needed.

### DLC, playset, or local-address inspection failed

Recognizable text includes `Could not enumerate DLCs`, `Could not read the active playset`, `Could not read loaded assemblies`, or `Could not enumerate local addresses`. The mod could not inspect part of the local installation or network environment. Restart once. If it repeats, include the full exception in a report; DLC matching, other-mod detection, or the address displayed to LAN players may be incomplete.

### Multiplayer screen or confirmation could not open

Recognizable text includes `Could not open the multiplayer menu screen`, `Could not open the game's world-selection screen`, or `Could not show the session-close confirmation dialog`. Reopen the screen and retry. If the multiplayer UI itself is missing, follow [Multiplayer UI did not load](#multiplayer-ui-did-not-load). A failed close-confirmation is canceled safely and does not disconnect the session.

### Session shutdown or world transition failed

Recognizable text includes `Session close on world transition failed`, `Failed to close the session while leaving the game`, `Returning the disconnected client to the main menu failed`, or `Graceful close failed`. The mod still attempts immediate socket cleanup and preserves a temporary shared-world copy while that world is open. If the client remains in the shared city, use [Could not close the shared city](#could-not-close-the-shared-city); otherwise restart before hosting or joining again.

## Synchronization and security log warnings

These messages normally appear only in logs. A single recovered warning does not always mean the session is unusable. Repeated warnings, visible divergence, or any `FAILED`/`aborted` error should be followed by Sync World. If it returns, stop the session and preserve the logs.

| Prefix or family | Meaning and response |
| --- | --- |
| `[security] Auth failure`, `Refused`, `Dropping`, `Disconnecting` | A password attempt, rate limit, message direction, size, channel, origin, or protocol rule was rejected. Expected for unwanted traffic; investigate if it names a trusted player repeatedly. |
| `Peer timed out`, `Handshake timed out`, transport send/receive queue warnings | A peer stopped responding or local networking could not keep up. Check connection stability and retry. |
| `Blob`, `map transfer`, `World sync`, `world recovery`, `resync barrier` | Full-world transfer or recovery was stale, malformed, stalled, or could not complete. Use Sync World and inspect both peers' logs. |
| `PrefabIndex`, `unknown prefab`, `unavailable prefab`, `no local match` | One machine could not resolve content named by another. Match DLC/mod content; if the own-risk setting is enabled, disable it and retry with identical playsets. |
| `BuildSync`, `ObjectTool`, `MoveSync`, `UpgradeSync`, `VisualCustomizationSync` | A building/object operation was malformed, unsupported, missed, or could not be applied atomically. Stop building that item, then Sync World. |
| `NetSync`, `NetApply`, `NetReplaceSync`, `NetUpgradeSync` | A road/network operation was incomplete, inconsistent, unsupported, rolled back, quarantined, or failed realization. Sync World; avoid repeating the exact road operation until the issue is reported. |
| `DeleteSync` | A remote deletion was stale, unmatched, malformed, or could not build its delete definition. Sync World if the object differs between players. |
| `AreaSync`, `ZoneSync`, `TerrainSync`, `TilePurchaseSync` | An area, zoning, terrain, or map-tile edit could not be captured/applied or exceeded a bounded queue. Pause edits, let the queue settle, then Sync World. |
| `RouteSync` | A route create/update/delete could not resolve or apply. Recreate the route after Sync World. |
| `GrowableSync`, `Occupancy`, `PropertyRent`, `ZoneDemand` | Simulation state drifted or a population/economy correction could not be captured/applied. Let the host run briefly and Sync World if visible values remain different. |
| `CityState`, `Statistics`, `PolicySync`, `NameSync`, `DevTree` | A city-state page or edit was malformed, deferred, skipped, or failed. Retry the action once; Sync World if the result differs. |
| `DisasterSync`, `TreeState`, weather/clock/speed channel warnings | A world-simulation event or state page could not be resolved. Sync World and avoid retriggering the same event if it repeats. |
| `realize FAILED`, `channel pump failed`, `queue overflowed`, `retry budget` | A bounded recovery path was exhausted. This is report-worthy, especially when repeated or visible in the city. |
| `Observer crashed` | One multiplayer observer threw an exception; the session continues, but part of synchronization may be impaired. Preserve the complete exception and flight log. |

## Normal messages that are not errors

- Multiplayer session ended means the host closed the session, the host left the city, or the connection ended. A client is returned to the main menu so its temporary shared-world copy cannot be mistaken for a normal local save.
- `Graceful close failed ... closing now` means the farewell message could not flush within the short shutdown window; sockets are still closed.
- `Session closed`, `peer left`, and `remote closed` can be normal when someone deliberately leaves.
- A one-time recovery message followed by a successful world sync means the safety system did its job.

## Reporting a problem

Include:

1. The exact UI headline and detail.
2. Whether each player used Steam Relay or Direct Connection.
3. Whether Ignore Mod Compatibility Checks (Own Risk) was enabled and which other mods were active.
4. The game and CS2 Multiplayer Mod versions from both computers.
5. `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\Player.log` and `CS2MP-flight.log` from the affected computers. Send the files after the problem occurs and before repeatedly restarting, because later runs can rotate diagnostic history.

Never post a session password. Network addresses and profile paths are redacted by the mod where it controls the log line, but review files before sharing them publicly.

[Back to Troubleshooting](troubleshooting.md)
