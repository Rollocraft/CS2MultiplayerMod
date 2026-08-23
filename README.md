# CS2 Multiplayer Mod

## Introduction

Feel free to join the development Discord server [here](https://discord.gg/KFZTW2YSJt).

This is a **multiplayer mod** for Cities: Skylines II. Join your friends and build a city together!

The mod is **experimental**. Back up your saves before hosting or joining, and expect bugs while development continues. 

## Requirements

- Cities: Skylines II (PC Version: Steam, XBox, Gamepass)
- **All players must run the same version of the mod.** Players will not be able to connect with mismatched versions. 
- Players should also have **matching gameplay DLC**. Radio Station DLC are unaffected. Learn how to disable DLC: [Disabling DLC](help/disable_dlc.md)
- There are **no** mods currently working.
- (Internet play only: Set up TCP Port Forwarding)

## Installation

The easiest way is through **Paradox Mods**: find the mod, add it to an **empty** playset, enable it, and restart the game if Cities: Skylines II asks you to.

[**PDXMods**](https://mods.paradoxplaza.com/mods/150432/Windows)

## Hosting a game

1. In the mod settings, set your player name and choose the host port, password, max players, LAN-only mode, and world re-sync interval.
2. Click **Multiplayer** on the main menu, then **Host Game**.

There is two ways to host: **Steam Relay**, which is suited for everyone who owns the game on Steam - and **Direct Connection**, which offers faster Sync times at the cost of setup time and can be used on XBox App, Microsoft Store and Game Pass versions of the game.

3. If you selected **Steam Relay**, copy the join code and send it to your friends. [Instructions for using **Direct Connection**](help/direct_connection.md).
3. Choose **Load World** for an existing city or **Create World** for a new one. If you use an existing save, **make a backup first**.
4. Finish the game's normal world selection. The multiplayer session starts automatically once the city is fully loaded.
5. If a city is already open, you can still start hosting from the in-game Multiplayer panel or the mod settings.

--- 

### Direct Connection

[Learn how to play via Direct Connection](help/direct_connection.md) (recommended for local play and best performance)

## Joining a game

1. Click **Multiplayer**, then **Join Game** from the main menu, or open the Join tab in the mod settings.
2. Enter the host address, port, your player name, and the password.
3. Click **Join Session**.
4. Wait while the host's city downloads and loads — larger cities take longer. The dialog closes itself once you're in.

## Troubleshooting

- **City looks out of sync?** Run `/sync` in chat, or click **Sync World Now** in the mod settings. Clients pull a fresh save from the host; the host refreshes every connected player.
- **Can't join (protocol mismatch)?** You and the host are on different mod versions. Update to the same build.

Check out **[Troubleshooting](help/troubleshooting.md)** for more issues.

## Technical Details

- The host is authoritative, and clients download the host's world when they join.
- The mod checks for matching DLC and version numbers before letting users connect. Find a list of white-listed DLC [here](CS2MultiplayerMod/Game/DlcCheck.cs#L21-L32). 

## Contributing & License

This mod and its source code are licensed under the [CS2 Multiplayer Mod Non-Commercial License](LICENSE). The license allows personal use, modification, and contributions to this project, but it does not allow commercial use, paid redistribution, or publishing clones/rebranded forks as someone else's project.

Contributions are welcome as long as they follow this repository's license. Keep attribution intact, submit changes through this project, and do not publish paid, monetized, or rebranded copies of the mod.

This project is not affiliated with, endorsed by, or sponsored by Colossal Order, Paradox Interactive or Iceflake Studios.
