---
title: Mods and compatibility
description: "Which mods are officially supported, how unsupported mods affect multiplayer, and how the compatibility check works."
extra_javascript:
  - assets/mod-table.js
---

# Mods and compatibility

This list combines **official support and community multiplayer testing**.

**Last updated:** September 17, 2026

## Compatibility list

<div class="mod-table" markdown>

| Status | Mod | Details | Tested by |
| --- | --- | --- | --- |
| 🛠️ Official | [Traffic](https://mods.paradoxplaza.com/mods/80095/Windows) | Fully supported. | CS2 Multiplayer Developers, J. M. S., DaStrobel, Janno |
| 🛠️ Official | [Road Speed Adjuster](https://mods.paradoxplaza.com/mods/125866/Windows) | Fully supported. | CS2 Multiplayer Developers |
| 🟢 Works | Anarchy | Works without known issues. | J. M. S., Janno |
| 🟢 Works | Building Use | No multiplayer issues observed in the tested modset. | Janno |
| 🟢 Works | Custom Chirps | No multiplayer issues observed in the tested modset. | Janno |
| 🟢 Works | [Extended Tooltip](https://mods.paradoxplaza.com/mods/78188/Windows) | Works for host and clients. | Tommy, Janno |
| 🟢 Works | I18n Everywhere | Works without known issues. | J. M. S., Janno |
| 🟢 Works | Find It | Works without known issues. | J. M. S. |
| 🟢 Works | Region Flag Icons | Works without known issues. | J. M. S. |
| 🟢 Works | Asset Icon Library | Works without known issues. | J. M. S. |
| 🟢 Works | Unified Icon Library | Works without known issues. | J. M. S., Janno |
| 🟢 Works | Extra Lib | Works without known issues. | J. M. S. |
| 🟢 Works | Industry Boundary | Works without known issues. | Tommy |
| 🟢 Works | All Transit + Trucks | Works without known issues. | Tommy |
| 🟢 Works | Lumina | Works without known issues. Cosmetic mod. | DaStrobel |
| 🟢 Works | [Stop Jaywalking](https://mods.paradoxplaza.com/mods/150664/Windows) | Works for host and clients as a client-side mod. | Tommy |
| 🟢 Works | [Road Name Remover](https://mods.paradoxplaza.com/mods/77463/Windows) | Works for host and clients as a client-side mod. | Tommy |
| 🟢 Works | [Achievement Fixer](https://mods.paradoxplaza.com/mods/121256/Windows) | Works for host and clients as a client-side mod. | Tommy |
| 🟢 Works | [Specialized Industry Freedom](https://mods.paradoxplaza.com/mods/155500/Windows) | Works for both host and clients. | Tommy |
| 🟢 Works | [Articulated Buses](https://mods.paradoxplaza.com/mods/148570/Windows) | Works for host and clients as a client-side mod. | Tommy |
| 🟢 Works | No Vehicle Despawn | No multiplayer issues observed in the tested modset. | Janno |
| 🟢 Works | Realistic JobSearch | No multiplayer issues observed in the tested modset. | Janno |
| 🟢 Works | Realistic Trips | No multiplayer issues observed in the tested modset. | Janno |
| 🟢 Works | Realistic Workplaces And Households | No multiplayer issues observed in the tested modset. | Janno |
| 🟢 Works | Traffic Tool Essentials | No multiplayer issues observed in the tested modset. | Janno |
| 🟢 Works | Official Region Packs | Official region packs such as the German and French packs were tested without problems. | J. M. S. |
| 🟡 Partially works | Move It | Works, but some functionality may only work correctly when used by the host. | J. M. S., Janno |
| 🟡 Partially works | Node Controller | Works for the host. Changes become visible to clients after a resync. Non-host players cannot move nodes themselves. | DaStrobel, J. M. S., Janno |
| 🟡 Partially works | Traffic Lights Enhancement | Works when used by the host. Changes become effective for clients after a resync. Non-host players cannot change intersection settings themselves. | DaStrobel |
| 🟡 Partially works | CoPaste | Works for the host. Usage by non-host players can cause desyncs and may require a forced resync. | Tommy |
| 🟡 Partially works | [529 Tiles](https://mods.paradoxplaza.com/mods/74328/Windows) | When using `Select initial starting tiles`, only the host can purchase the free starting tiles. Afterwards non-host players can purchase tiles normally. | Tommy |
| 🟡 Partially works | [Change Company](https://mods.paradoxplaza.com/mods/114101/Windows) | Only the host can use and configure the mod and affected buildings. | Tommy |
| 🟡 Partially works | [Event Rush](https://mods.paradoxplaza.com/mods/156682/Windows) | Citizens created by the mod are visible to both host and clients. Very large events can cause significant performance problems. | Tommy |
| 🟡 Partially works | Decals / Props | Placement works, but changes are not automatically synchronized. A manual synchronization makes them visible to other players. | KeKo |
| 🔴 Doesn't work | Better Bulldozer | Bulldozing objects can cause connected players to crash. | KeKo |

</div>

**Mods by Gruny:** J. M. S. reported that mods made by **Gruny** generally appeared to work without
problems. Because individual mods were not listed separately in the report, they are not
individually marked as verified above.

## Report a mod

Please only report mods you have actually tested in multiplayer, in this format:

```text
🟢 / 🟡 / 🔴 Mod Name
🔗 Paradox Mods Link
Status: Works / Partially Works / Doesn't Work
Details: What works, what doesn't, and whether there are host/client limitations.
```

## Other mods are blocked

Hosting and joining are blocked while any unsupported mod is active, and a host rejects
players running a different CS2 Multiplayer Mod build. Nothing in the synchronization layer
accounts for an unverified third party changing prefabs, tools or the simulation, so one
unsupported mod on one machine is enough to desync the session or crash the other player.

The check reads your active Paradox Mods playset. That includes asset-only mods such as
maps, prop packs and prefab packs, which load no code at all. Mods in your other playsets
are not enabled for this run and are ignored.

When a player joins, the host also compares both players' other active mods, including
their versions, and rejects the join with the exact difference. This applies to the
supported mods above too: Traffic, Anarchy or Road Speed Adjuster on only one computer
changes what the game builds there. Find It and Asset Icon Library only change what one
player sees and are not compared.

| Banner | Meaning |
| --- | --- |
| Other Mods Enabled | Host and Join are blocked; the listed mods have to be disabled |
| Other Mods Enabled, still loaded | Already disabled in the playset, but still in memory - restart the game once |
| Compatibility Check Ignored | Other mods are active and the own-risk override is on |

To clear the block:

1. Disable every unsupported mod in your active playset. A playset that contains only CS2
   Multiplayer Mod plus Traffic, Road Speed Adjuster, Anarchy, Find It and Asset Icon
   Library is allowed; every other mod in the list above still needs the override below.
2. Go back to the game and wait a few seconds for the banner to clear.
3. If the banner says the mods are still loaded, restart the game.

---

## Turning the check off

Options ▸ CS2 Multiplayer Mod ▸ Advanced ▸ Compatibility ▸ Ignore Mod Compatibility Checks
(Own Risk). It can only be changed while offline, before hosting or joining.

With it on, other active mods no longer block hosting or joining on your machine, and a
host also admits players on a different CS2 Multiplayer Mod build or with different other
mods, as long as the network protocol matches.

It does not bypass:

- the network protocol check, because different builds can encode network data differently,
- the Cities: Skylines II version check, or
- the DLC check.

It also does not make another mod multiplayer-aware. Use the same playset
on every computer where possible, and expect desyncs, missing prefabs, broken cities or
crashes. The host decides whether different multiplayer-mod builds and different mod sets
are admitted; each player decides whether their own extra mods are allowed locally.

---

## Other mod compatibility

Mods not listed under official support are unverified and remain blocked by default. This
includes mods that may appear to be display-only or UI-only: without verification, the
multiplayer developers cannot guarantee that they will not change synchronized state.

---

[Back to troubleshooting.](troubleshooting.md)
