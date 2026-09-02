---
title: Troubleshooting guide
description: "Fixes for the problems players hit most often: joining, desyncs, crashes and world reloads."
---

# Troubleshooting guide

Having issues? This guide covers the problems players hit most often. Updated
`2026-08-23` for version `v0.1.6` 


## Pick your problem

<div class="cards">

  <div class="card">
    <span class="card__icon"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20h.01"/><path d="M8.5 16.429a5 5 0 0 1 7 0"/><path d="M5 12.859a10 10 0 0 1 5.17-2.69"/><path d="M19 12.859a10 10 0 0 0-2.007-1.523"/><path d="M2 8.82a15 15 0 0 1 4.177-2.643"/><path d="M22 8.82a15 15 0 0 0-11.288-3.764"/><path d="m2 2 20 20"/></svg></span>
    <span class="card__title">I cannot connect</span>
    <ul class="card__links">
      <li><a href="#connection-issues">Connection issues</a></li>
      <li><a href="../steam-relay/">Steam Relay</a></li>
      <li><a href="../direct-connection/">Direct connection</a></li>
      <li><a href="../forwarding_troubleshoot/">Port forwarding problems</a></li>
    </ul>
  </div>

  <div class="card">
    <span class="card__icon"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12.586 2.586A2 2 0 0 0 11.172 2H4a2 2 0 0 0-2 2v7.172a2 2 0 0 0 .586 1.414l8.704 8.704a2.426 2.426 0 0 0 3.42 0l6.58-6.58a2.426 2.426 0 0 0 0-3.42z"/><circle cx="7.5" cy="7.5" r=".5" fill="currentColor"/></svg></span>
    <span class="card__title">Versions do not match</span>
    <ul class="card__links">
      <li><a href="#mod-version-issues">Mod version issues</a></li>
      <li><a href="#game-version-issues">Game version issues</a></li>
      <li><a href="../verify_files/">Verify game files</a></li>
    </ul>
  </div>

  <div class="card">
    <span class="card__icon"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m7.5 4.27 9 5.15"/><path d="M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z"/><path d="m3.3 7 8.7 5 8.7-5"/><path d="M12 22V12"/></svg></span>
    <span class="card__title">Content does not match</span>
    <ul class="card__links">
      <li><a href="#dlc-mismatch-issues">DLC mismatch issues</a></li>
      <li><a href="../disable_dlc/">Disable DLC</a></li>
      <li><a href="#other-mods-enabled">Other mods enabled</a></li>
    </ul>
  </div>

  <div class="card">
    <span class="card__icon"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M7.9 20A9 9 0 1 0 4 16.1L2 22z"/><path d="M12 8v4"/><path d="M12 16h.01"/></svg></span>
    <span class="card__title">The game showed an error</span>
    <ul class="card__links">
      <li><a href="#troubleshooting-by-error-message">Troubleshoot by error message</a></li>
      <li><a href="../errors-and-warnings/">Complete error reference</a></li>
      <li><a href="#menu-issues">"Join Game" is missing</a></li>
    </ul>
  </div>

</div>

## Connection Issues

First: both players have to use the same connection type. See
[Steam Relay](steam-relay.md) and [Direct connection](direct-connection.md).

### Steam Relay

- The host needs the Steam copy of the game, launched through Steam, with Steam online.
  Xbox App, Microsoft Store and Game Pass copies cannot host over the relay.
- The join code is all a joining player needs - no address, no port, no router setup.
- If the host screen shows "Unavailable - start the game through Steam", restart the game
  from Steam, or switch both players to a direct connection.

### Direct connection, host side

- The mod asks your router to open the port when hosting starts. If the hosting status
  asks you to forward it yourself, follow [Set up port forwarding](forwarding.md).
- Allow the port through the Windows Firewall and any antivirus.
- LAN Only refuses everything that is not from your local network. Switch it off for
  internet play.

[Issues with port forwarding?](forwarding_troubleshoot.md)

### Direct connection, joining side

- Use the host's local IP on the same network, and their public IP over the internet. The
  port has to match the host's exactly.
- Check that your connection is not blocked by antivirus or your local firewall, and that
  you are online.

## Mod Version Issues

Check that you have the same mod version as the people you are trying to play with. Update the mod via Paradox Mods (PDXMods) to the newest version.

Still having issues? Remove the mod on PDXMods. Restart the game. Reinstall the mod on PDXMods. Restart the game.

## Game Version Issues

Check that you have the same game version as the people you are trying to play with. You can find the game version in the bottom left of the Main Menu when you start the game. The beginning should look similar to this: `1.6.0f1`. If not, update your game through Steam or XBox/Gamepass.

Still having issues? [Verify Game Files](verify_files.md) (Steam: Right-click Game => Properties => Installed Files => Verify Integrity of game files; [XBox/Gamepass (click)](verify_files.md)).

## DLC Mismatch Issues

Check that you have the same DLC enabled as the people you are trying to play with. [Learn how to disable DLC.](disable_dlc.md)

Cannot disable the CS1 Treasure Hunt DLC? It is ignored by the mod, so it never blocks a
join.

## Other Mods Enabled

Hosting and joining are blocked while any other mod is active in your Paradox Mods
playset - including asset-only mods such as maps, prop packs and prefab packs.

1. Disable every mod except CS2 Multiplayer Mod in your active playset. A playset with only
   this mod is the safest setup.
2. Wait a few seconds; the banner clears on its own.
3. If it says the mods are *still loaded*, restart the game once.

Advanced users can turn the check off under Options ▸ CS2 Multiplayer Mod ▸ General ▸
Ignore Mod Compatibility Checks (Own Risk), which permits other mods and mixed mod builds
at the cost of desyncs, broken cities and crashes. Full details:
[Mods and compatibility](mods.md).

## Menu issues

Go to options. Check that CS2MultiplayerMod appears in settings. If not:

Remove the mod on PDXMods. Restart the game. Reinstall the mod on PDXMods. Restart the game.

Check that you do not have any [launch options](https://cs2.paradoxwikis.com/Launch_Parameters) preventing the mod from working. Check that the mod is in your active playset on PDXMods.

The mod options carry a Host tab and a Join tab that can start or join a session without the
in-game screens.

![](assets/img/ui-options-general.png)

!!! warning "Those tabs are only a fallback"

    If you need them, the multiplayer screens never rendered, which means another mod broke
    the game's UI during startup. We cannot tell you which mod it was - remove the other
    mods and the normal screens come back. See
    [Multiplayer UI did not load](errors-and-warnings.md#multiplayer-ui-did-not-load).

## Troubleshooting by Error message

Every in-game multiplayer error now includes an Open Help action that targets the relevant guide. For a searchable list of every player-facing error, warning banner, save/exit failure, and multiplayer log-warning family, see the [Error and Warning Reference](errors-and-warnings.md).
