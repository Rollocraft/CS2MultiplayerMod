import { trigger } from "cs2/api";
import { ModRegistrar } from "cs2/modding";
import { extendCreditsScreen, MultiplayerMenuButton } from "mods/join-game";
import { MultiplayerRightMenuButton } from "mods/mp-hub";
import { GameJoinLoadingScreen, MenuJoinLoadingScreen } from "mods/loading-screen";
import {
    GameSessionDisconnectConfirmation,
    MenuSessionDisconnectConfirmation,
} from "mods/session-confirmation";

// Vanilla-internal module hosting the main-menu button column. Game updates can
// rename it, so registration falls back to the official "Menu" append hook.
const MAIN_MENU_MODULE = "game-ui/menu/components/main-menu-screen/main-menu-screen.tsx";
const CREDITS_SCREEN_MODULE = "game-ui/menu/components/credits-screen/credits-screen.tsx";

const register: ModRegistrar = (moduleRegistry) => {
    // Tell the C# side this module survived the game's UI-module load chain.
    // MultiplayerUISystem warns in the log if this never arrives (e.g. another
    // mod's broken .mjs crashed the chain before reaching us — see Gooee).
    try {
        trigger("cs2mp", "uiReady");
    } catch {
        // Binding not reachable; the C# watchdog will report the module missing.
    }

    // Root owners cover joins started through Options and keep the overlay alive
    // across the menu-to-game world hand-off. The native Multiplayer sub-screen
    // also mounts its own explicitly owned instance for the initial connection.
    // Each renders only a Portal while connecting/syncing.
    try {
        moduleRegistry.append("Menu", MenuJoinLoadingScreen);
        moduleRegistry.append("Menu", MenuSessionDisconnectConfirmation);
    } catch (e) {
        console.warn("[cs2mp] menu connection view could not be registered.", e);
    }
    try {
        moduleRegistry.append("Game", GameJoinLoadingScreen);
        moduleRegistry.append("Game", GameSessionDisconnectConfirmation);
    } catch (e) {
        console.warn("[cs2mp] in-game connection view could not be registered.", e);
    }

    // In-game multiplayer hub: the right-menu column renders the official
    // "GameBottomRight" modding hook directly above the notification/Chirper
    // buttons, so this lands exactly on top of the bird icon in vanilla style.
    try {
        moduleRegistry.append("GameBottomRight", MultiplayerRightMenuButton);
    } catch (e) {
        console.warn("[cs2mp] GameBottomRight append failed; in-game hub button unavailable.", e);
    }

    // The Credits entry is a native menu-screen slot that is otherwise inactive
    // when Multiplayer is opened. Replacing its component conditionally puts our
    // screen inside the exact same focus and transition stack as New/Load Game.
    let multiplayerScreenRegistered = false;
    try {
        if (moduleRegistry.registry.has(CREDITS_SCREEN_MODULE)) {
            moduleRegistry.extend(CREDITS_SCREEN_MODULE, "CreditsScreen", extendCreditsScreen);
            multiplayerScreenRegistered = true;
        }
    } catch (e) {
        console.warn("[cs2mp] native Credits screen extension failed.", e);
    }
    if (!multiplayerScreenRegistered) {
        console.warn("[cs2mp] Multiplayer screen unavailable: native Credits module was not found.");
    }

    // Insert a "Multiplayer" button into the vanilla main-menu button column,
    // after Continue / New Game / Load Game (index 3).
    try {
        if (moduleRegistry.registry.has(MAIN_MENU_MODULE)) {
            if (multiplayerScreenRegistered) {
                moduleRegistry.append(MAIN_MENU_MODULE, "MainMenuNavigation", MultiplayerMenuButton, 3);
            }
            return;
        }
        console.warn("[cs2mp] " + MAIN_MENU_MODULE + " not in module registry; using generic Menu hook.");
    } catch (e) {
        console.warn("[cs2mp] main-menu append failed; using generic Menu hook.", e);
    }
    if (multiplayerScreenRegistered) {
        moduleRegistry.append("Menu", MultiplayerMenuButton);
    }
};

export default register;
