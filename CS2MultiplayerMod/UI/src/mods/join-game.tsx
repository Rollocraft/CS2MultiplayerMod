import { bindValue, trigger, useValue } from "cs2/api";
import { AutoNavigationScope, BackConsumer, NavigationDirection } from "cs2/input";
import { useLocalization } from "cs2/l10n";
import { getModule } from "cs2/modding";
import { Button, DialogContext, DialogStack, MenuButton } from "cs2/ui";
import { CSSProperties, ReactNode, useContext, useEffect, useRef, useState } from "react";
import {
    CONNECTION_DIRECT,
    CONNECTION_LOC,
    CONNECTION_RELAY,
    ConnectionDropdown,
    JoinCodeDisplay,
} from "mods/connection-picker";
import { DisclaimerModal, disclaimerAccepted$ } from "mods/disclaimer";
import { MultiplayerJoinLoadingScreen } from "mods/loading-screen";
import { OtherModsBanner, useModsBlocked } from "mods/mods-banner";
import { MULTIPLAYER_BLUE } from "mods/multiplayer-theme";
import { VersionWarningBanner } from "mods/version-banner";

// Binding group shared with MultiplayerUISystem on the C# side. The field values
// live in the mod's Setting object, so this screen, the in-game hub and Options
// all share the same multiplayer data.
const GROUP = "cs2mp";

// Locale keys served by the mod's LocaleEN/LocaleDE dictionary sources (constants
// in L10n.Key on the C# side). The game resolves them against its active language;
// the inline fallbacks only cover a dictionary that has not loaded yet.
const LOC = {
    multiplayer: "CS2MP.UI.Multiplayer",
    joinGame: "CS2MP.UI.JoinGame",
    hostGame: "CS2MP.UI.HostGame",
    hostWorldTitle: "CS2MP.UI.HostWorldTitle",
    loadWorld: "CS2MP.UI.LoadWorld",
    createWorld: "CS2MP.UI.CreateWorld",
    dialogTitle: "CS2MP.UI.DialogTitle",
    playerName: "CS2MP.UI.PlayerName",
    hostAddress: "CS2MP.UI.HostAddress",
    port: "CS2MP.UI.Port",
    password: "CS2MP.UI.Password",
    join: "CS2MP.UI.Join",
    disconnect: "CS2MP.UI.Disconnect",
    ...CONNECTION_LOC,
};

// translate() is typed string | null; this narrows it to the English fallback so
// JSX/props that require a string stay clean.
const useT = () => {
    const { translate } = useLocalization();
    return (id: string, fallback: string) => translate(id, fallback) ?? fallback;
};

const playerName$ = bindValue<string>(GROUP, "playerName", "Player");
const address$ = bindValue<string>(GROUP, "joinAddress", "127.0.0.1");
const port$ = bindValue<string>(GROUP, "joinPort", "25001");
const password$ = bindValue<string>(GROUP, "joinPassword", "");
const statusKind$ = bindValue<string>(GROUP, "statusKind", "offline");
const inSession$ = bindValue<boolean>(GROUP, "inSession", false);
// The same native save-list binding that enables/disables the game's Load Game
// menu item. Using it keeps our Load World choice in exact lockstep with vanilla.
const savedGames$ = bindValue<unknown[]>("menu", "saves", []);
const multiplayerMenuActive$ = bindValue<boolean>(GROUP, "multiplayerMenuActive", false);
const hostConnection$ = bindValue<string>(GROUP, "hostConnection", CONNECTION_RELAY);
const joinCode$ = bindValue<string>(GROUP, "joinCode", "");
const relayAvailable$ = bindValue<boolean>(GROUP, "relayAvailable", false);
// False on copies of the game that ship no Steam library (Microsoft Store / Game
// Pass): there is no relay to pick, so the choice itself is left out.
const relaySupported$ = bindValue<boolean>(GROUP, "relaySupported", false);
const relayUnavailableReason$ = bindValue<string>(GROUP, "relayUnavailableReason", "");
const joinConnection$ = bindValue<string>(GROUP, "joinConnection", CONNECTION_RELAY);
const joinCodeInput$ = bindValue<string>(GROUP, "joinCodeInput", "");

const openMultiplayerScreen = () => trigger(GROUP, "openMultiplayerScreen");

// ---- Vanilla menu-screen chrome ------------------------------------------------
// The Load Game / New Game screens are built from shared modules in the game's UI
// module registry: a centered 1760x980rem content container ("menu-ui") holding a
// sub-screen (back arrow + large title + content). Reusing them keeps this screen's
// sizing and layout identical to those screens. The paths are vanilla-internal and
// can move on a game update, hence the inline fallbacks that replicate the same
// geometry.
const tryModule = (path: string, exportName: string): any => {
    try {
        return getModule(path, exportName);
    } catch {
        return null;
    }
};
const VanillaSubScreen = tryModule("game-ui/menu/components/shared/sub-screen/sub-screen.tsx", "SubScreen");
const VanillaTransitionGroup = tryModule(
    "game-ui/common/animations/transition-group-coordinator.tsx",
    "TransitionGroupCoordinator",
);
const VanillaClassNameTransition = tryModule(
    "game-ui/common/animations/class-name-transition.tsx",
    "ClassNameTransition",
);
const VanillaFocusScope = tryModule("game-ui/common/focus/focus-scope.tsx", "FocusScope");
const shrinkFadeStyles: Record<string, string> | null =
    tryModule("game-ui/menu/transitions/shrink-fade.module.scss", "classes");
const playUISound = tryModule("game-ui/common/data-binding/audio-bindings.ts", "playUISound");
const UISound = tryModule("game-ui/common/data-binding/audio-bindings.ts", "UISound");
const subScreenClasses: Record<string, string> | null =
    tryModule("game-ui/menu/components/shared/sub-screen/sub-screen.module.scss", "classes");
const childOpacityTransitionClass = subScreenClasses?.header
    ?.split(/\s+/)
    .find((className) => className.startsWith("child-opacity-transition"));
// The game scales its UI by adjusting the root font size, so rem behaves like
// resolution-independent pixels; all sizes below follow that convention.
const styles: Record<string, CSSProperties> = {
    pageHost: {
        width: "100%",
        height: "100%",
        position: "relative",
    },
    page: {
        position: "absolute",
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
    },
    pageContent: {
        width: "100%",
        height: "100%",
    },
    // Fallback sub-screen chrome (back arrow + large title), same metrics as the
    // vanilla sub-screen header.
    fallbackScreenRoot: {
        position: "absolute",
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        display: "flex",
        flexDirection: "column",
        alignItems: "stretch",
    },
    fallbackHeader: {
        display: "flex",
        flexDirection: "row",
        alignItems: "center",
        marginBottom: "8rem",
    },
    fallbackBackButton: {
        width: "40rem",
        height: "40rem",
        marginRight: "12rem",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
    },
    fallbackBackIcon: {
        width: "24rem",
        height: "24rem",
    },
    fallbackTitle: {
        fontSize: "40rem",
        lineHeight: "1.2",
        fontWeight: "bold",
        color: "var(--menuTitleNormal, #ffffff)",
        textTransform: "uppercase",
    },
    fallbackContent: {
        flexGrow: 1,
        flexShrink: 1,
        flexBasis: "0%",
        minHeight: 0,
        position: "relative",
    },
    // Large native-button choices used by both the multiplayer landing page and
    // the host-world picker. The game's flat button supplies hover, focus, press,
    // disabled and controller states; only the card geometry is ours.
    choiceArea: {
        height: "100%",
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
        paddingBottom: "48rem",
    },
    choiceWarning: {
        width: "920rem",
        maxWidth: "88%",
        marginBottom: "20rem",
    },
    choiceRow: {
        width: "980rem",
        maxWidth: "90%",
        height: "350rem",
        display: "flex",
        alignItems: "stretch",
        justifyContent: "center",
    },
    choiceButton: {
        flex: "1 1 0%",
        minWidth: 0,
        height: "100%",
        margin: "0 16rem",
        padding: "34rem",
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
        boxShadow: "0 14rem 38rem rgba(0, 0, 0, 0.38)",
        pointerEvents: "auto",
    },
    choiceIconFrame: {
        width: "184rem",
        height: "184rem",
        flexShrink: 0,
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        marginBottom: "28rem",
        borderRadius: "92rem",
        backgroundColor: "rgba(0, 0, 0, 0.22)",
        border: "2rem solid rgba(157, 193, 222, 0.22)",
    },
    choiceIcon: {
        width: "112rem",
        height: "112rem",
        objectFit: "contain",
        // The glyph set mixes explicit white fills with default black fills.
        // Normalize both to the menu's white icon color, as native tinted icons do.
        filter: "brightness(0) invert(1) drop-shadow(0 4rem 8rem rgba(0, 0, 0, 0.35))",
    },
    choiceLabel: {
        color: "#ffffff",
        fontSize: "28rem",
        lineHeight: "1.2",
        fontWeight: "bold",
        textAlign: "center",
        textTransform: "uppercase",
    },
    // The join form panel inside the screen's content area.
    contentArea: {
        height: "100%",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
    },
    panel: {
        width: "760rem",
        maxWidth: "85%",
        backgroundColor: MULTIPLAYER_BLUE,
        borderRadius: "4rem",
        padding: "32rem",
        boxShadow: "0 16rem 48rem rgba(0, 0, 0, 0.45)",
        pointerEvents: "auto",
    },
    row: {
        display: "flex",
        alignItems: "center",
        marginBottom: "16rem",
    },
    label: {
        width: "200rem",
        fontSize: "17rem",
        color: "#9dc1de",
        textTransform: "uppercase",
    },
    input: {
        flex: 1,
        fontSize: "18rem",
        color: "#ffffff",
        backgroundColor: "rgba(0, 0, 0, 0.35)",
        border: "1rem solid rgba(157, 193, 222, 0.35)",
        borderRadius: "3rem",
        padding: "9rem 12rem",
    },
    inputDisabled: {
        opacity: 0.55,
        cursor: "not-allowed",
    },
    buttons: {
        display: "flex",
        justifyContent: "flex-end",
        marginTop: "24rem",
    },
    button: {
        marginLeft: "12rem",
        padding: "10rem 28rem",
        fontSize: "17rem",
    },
    // Connection picker sitting above the Load/Create tiles on the host screen. It
    // shares choiceRow's geometry and the tiles' 16rem side margin so its edges line
    // up with them exactly at any resolution.
    connectionRail: {
        width: "980rem",
        maxWidth: "90%",
        display: "flex",
        marginBottom: "20rem",
    },
    connectionPanel: {
        flex: "1 1 0%",
        minWidth: 0,
        margin: "0 16rem",
        backgroundColor: MULTIPLAYER_BLUE,
        borderRadius: "4rem",
        padding: "16rem 20rem",
        boxShadow: "0 10rem 28rem rgba(0, 0, 0, 0.38)",
        pointerEvents: "auto",
    },
    connectionRow: {
        display: "flex",
        alignItems: "center",
    },
    connectionSpacer: {
        height: "12rem",
    },
    dropdownToggle: {
        minWidth: "260rem",
    },
    optionFallback: {
        display: "block",
        width: "100%",
        padding: "9rem 14rem",
        fontSize: "17rem",
        textAlign: "left",
    },
    hint: {
        fontSize: "15rem",
        lineHeight: "1.35",
        color: "rgba(157, 193, 222, 0.85)",
        marginTop: "10rem",
    },
    hintWarning: {
        fontSize: "15rem",
        lineHeight: "1.35",
        color: "#ffb454",
        marginTop: "10rem",
    },
    codeInput: {
        flex: 1,
        fontSize: "20rem",
        letterSpacing: "1rem",
        color: "#ffffff",
        backgroundColor: "rgba(0, 0, 0, 0.35)",
        border: "1rem solid rgba(157, 193, 222, 0.35)",
        borderRadius: "3rem",
        padding: "9rem 12rem",
    },
};

interface FieldProps {
    label: string;
    value: string;
    secret?: boolean;
    disabled?: boolean;
    onChange: (value: string) => void;
}

const Field = ({ label, value, secret, disabled, onChange }: FieldProps) => {
    const [draft, setDraft] = useState(value);
    const [editing, setEditing] = useState(false);

    useEffect(() => {
        if (!editing) setDraft(value);
    }, [value]);

    const updateValue = (next: string) => {
        setDraft(next);
        onChange(next);
    };

    return (
        <div style={styles.row}>
            <div style={styles.label}>{label}</div>
            <input
                type={secret ? "password" : "text"}
                style={disabled ? { ...styles.input, ...styles.inputDisabled } : styles.input}
                value={draft}
                disabled={disabled}
                spellCheck={false}
                autoComplete="off"
                onFocus={() => setEditing(true)}
                onBlur={() => {
                    setEditing(false);
                    if (draft !== value) onChange(draft);
                }}
                onMouseDown={(e) => e.stopPropagation()}
                onKeyDown={(e) => {
                    // Let Escape reach the native BackConsumer even while a text
                    // field is active; keep gameplay/menu shortcuts out otherwise.
                    if (e.key !== "Escape") e.stopPropagation();
                }}
                onChange={(e) => updateValue((e.target as HTMLInputElement).value)}
            />
        </div>
    );
};

interface ChoiceTileProps {
    focusKey: string;
    icon: string;
    label: string;
    disabled?: boolean;
    onSelect: () => void;
}

const ChoiceTile = ({ focusKey, icon, label, disabled, onSelect }: ChoiceTileProps) => (
    <Button
        variant="flat"
        focusKey={focusKey}
        disabled={disabled}
        style={styles.choiceButton}
        onSelect={onSelect}>
        <div style={disabled ? { ...styles.choiceIconFrame, opacity: 0.42 } : styles.choiceIconFrame}>
            <img src={icon} style={styles.choiceIcon} />
        </div>
        <div style={disabled
            ? { ...styles.choiceLabel, color: "rgba(255, 255, 255, 0.42)" }
            : styles.choiceLabel}>
            {label}
        </div>
    </Button>
);

/**
 * Host connection picker: relay (default) or a direct port. In relay mode the code
 * players need is shown right here, because that is the only thing they have to be
 * given - there is no address, no port and nothing to forward.
 */
const ConnectionPicker = () => {
    const t = useT();
    const mode = useValue(hostConnection$);
    const code = useValue(joinCode$);
    const relayAvailable = useValue(relayAvailable$);
    const relaySupported = useValue(relaySupported$);
    const relayReason = useValue(relayUnavailableReason$);

    const relay = relaySupported && mode !== CONNECTION_DIRECT;

    return (
        <div style={styles.connectionRail}>
        <div style={styles.connectionPanel} onMouseDown={(e) => e.stopPropagation()}>
            {relaySupported && (
                <div style={styles.connectionRow}>
                    <div style={styles.label}>{t(LOC.mode, "Connection")}</div>
                    <ConnectionDropdown
                        value={mode}
                        style={styles.dropdownToggle}
                        onChange={(value) => trigger(GROUP, "setHostConnection", value)}
                    />
                </div>
            )}

            {relay && relayAvailable && (
                <>
                    <div style={styles.connectionSpacer} />
                    <div style={styles.connectionRow}>
                        <div style={styles.label}>{t(LOC.joinCode, "Join Code")}</div>
                        <JoinCodeDisplay code={code} style={styles.codeInput} />
                    </div>
                </>
            )}

            <div style={relay && !relayAvailable ? styles.hintWarning : styles.hint}>
                {relay
                    ? relayAvailable
                        ? `${t(LOC.joinCodeHint, "Send this code to your friends. They pick Steam Relay on their Join screen and enter it.")} ${t(LOC.joinCodeSelectHint, "Click the code to select it, then press Ctrl+C.")}`
                        : `${t(LOC.relayUnavailableHint, "Steam is not available right now, so relay hosting cannot start. Use a direct connection instead.")}${relayReason ? ` (${relayReason})` : ""}`
                    : t(LOC.directHint, "Players connect to your address and port. Needs the port forwarded on your router.")}
            </div>
        </div>
        </div>
    );
};

const ChoiceScreen = ({
    focusKey,
    debugName,
    initialFocused,
    header,
    children,
}: {
    focusKey: string | number;
    debugName: string;
    initialFocused: string;
    header?: ReactNode;
    children: ReactNode;
}) => (
    <div style={styles.choiceArea}>
        <OtherModsBanner style={styles.choiceWarning} />
        <VersionWarningBanner style={styles.choiceWarning} />
        {header}
        <AutoNavigationScope
            focusKey={focusKey}
            debugName={debugName}
            direction={NavigationDirection.Horizontal}
            initialFocused={initialFocused}
            allowLooping>
            <div style={styles.choiceRow}>{children}</div>
        </AutoNavigationScope>
    </div>
);

type MultiplayerView = "choice" | "join" | "host";

const PAGE_INDEX: Record<MultiplayerView, number> = {
    choice: 0,
    join: 1,
    host: 2,
};

interface NativeMenuScreenProps {
    focusKey: string | number;
    className?: string;
    onClose: () => void;
}

const playOpenMenuSound = () => {
    try {
        if (typeof playUISound === "function" && UISound?.openMenu !== undefined) {
            playUISound(UISound.openMenu);
        }
    } catch {
        // The navigation itself remains available if the audio module changes.
    }
};

export const MultiplayerScreenRenderer = ({ focusKey, className, onClose }: NativeMenuScreenProps) => {
    const t = useT();
    const playerName = useValue(playerName$);
    const address = useValue(address$);
    const port = useValue(port$);
    const password = useValue(password$);
    const statusKind = useValue(statusKind$);
    const inSession = useValue(inSession$);
    const joinConnection = useValue(joinConnection$);
    const joinCodeInput = useValue(joinCodeInput$);
    const hasSavedGame = useValue(savedGames$).length > 0;
    // Any other live mod blocks a session outright: the banner explains it and every
    // control that would start one is disabled while it is set.
    const modsBlocked = useModsBlocked();
    const relaySupported = useValue(relaySupported$);
    const joinIsRelay = relaySupported && joinConnection !== CONNECTION_DIRECT;
    const [view, setView] = useState<MultiplayerView>("choice");

    // Keep the multiplayer marker alive through the native exit animation. Once
    // this screen is actually removed, the Credits slot can behave normally again.
    useEffect(() => () => {
        try {
            trigger(GROUP, "multiplayerScreenExited");
        } catch {
            // The UI is already shutting down.
        }
    }, []);

    // Auto-close once a join we started here actually completes (the world has loaded
    // and gameplay is live → statusKind flips "connecting" → "connected"). Guarded by
    // a "did we go through connecting?" flag so opening the dialog while already in a
    // session (to disconnect) does not instantly close it.
    const sawConnecting = useRef(false);
    useEffect(() => {
        if (statusKind === "connecting") {
            sawConnecting.current = true;
        } else if (statusKind === "connected" && sawConnecting.current) {
            sawConnecting.current = false;
            onClose();
        }
    }, [statusKind, onClose]);

    const title = view === "choice"
        ? t(LOC.multiplayer, "Multiplayer")
        : view === "host"
            ? t(LOC.hostWorldTitle, "Choose a World")
            : t(LOC.dialogTitle, "Join Multiplayer Game");

    const openView = (nextView: MultiplayerView) => {
        setView(nextView);
        playOpenMenuSound();
    };

    const backAction = view === "choice"
        ? onClose
        : () => openView("choice");

    const openHostWorld = (action: "hostLoadWorld" | "hostCreateWorld") => {
        // The C# trigger both arms automatic hosting and selects the real Load/New
        // Game screen. From here the native menu coordinator owns the transition.
        trigger(GROUP, action);
    };

    const joinForm = (
        <div style={styles.contentArea}>
            <AutoNavigationScope
                focusKey={PAGE_INDEX.join}
                debugName="CS2MP Join Game Screen"
                direction={NavigationDirection.Both}
                initialFocused={inSession ? "disconnect" : "join"}
                allowLooping>
                <div style={styles.panel} onMouseDown={(e) => e.stopPropagation()}>
                    <VersionWarningBanner />
                    <Field
                        label={t(LOC.playerName, "Player Name")}
                        value={playerName}
                        onChange={(v) => trigger(GROUP, "setPlayerName", v)}
                    />
                    {relaySupported && (
                        <div style={styles.row}>
                            <div style={styles.label}>{t(LOC.mode, "Connection")}</div>
                            <ConnectionDropdown
                                value={joinConnection}
                                disabled={inSession}
                                style={styles.dropdownToggle}
                                onChange={(v) => trigger(GROUP, "setJoinConnection", v)}
                            />
                        </div>
                    )}
                    {/* Relay joins carry no address or port: the code is the whole target,
                        so asking for the other two would only be more to get wrong. */}
                    {joinIsRelay ? (
                        <Field
                            label={t(LOC.joinCodeEntry, "Join Code")}
                            value={joinCodeInput}
                            disabled={inSession}
                            onChange={(v) => trigger(GROUP, "setJoinCodeInput", v)}
                        />
                    ) : (
                        <>
                            <Field
                                label={t(LOC.hostAddress, "Host Address")}
                                value={address}
                                onChange={(v) => trigger(GROUP, "setJoinAddress", v)}
                            />
                            <Field
                                label={t(LOC.port, "Port")}
                                value={port}
                                onChange={(v) => trigger(GROUP, "setJoinPort", v)}
                            />
                        </>
                    )}
                    <Field
                        label={t(LOC.password, "Password")}
                        secret
                        disabled={inSession}
                        value={password}
                        onChange={(v) => trigger(GROUP, "setJoinPassword", v)}
                    />
                    <div style={styles.buttons}>
                        {inSession ? (
                            <Button
                                variant="primary"
                                style={styles.button}
                                focusKey="disconnect"
                                onSelect={() => trigger(GROUP, "disconnect")}>
                                {t(LOC.disconnect, "Disconnect")}
                            </Button>
                        ) : (
                            <Button
                                variant="primary"
                                style={styles.button}
                                focusKey="join"
                                onSelect={() => trigger(GROUP, "join")}>
                                {t(LOC.join, "Join")}
                            </Button>
                        )}
                    </div>
                </div>
            </AutoNavigationScope>
        </div>
    );

    const content = view === "choice" ? (
        <ChoiceScreen
            focusKey={PAGE_INDEX.choice}
            debugName="CS2MP Multiplayer Choice"
            initialFocused="join-game">
            {/* Greyed out for the same reason as Host Game, rather than letting the
                player into the form to find Join disabled there. Still reachable while
                a session runs: this tile is the only route to Disconnect. */}
            <ChoiceTile
                focusKey="join-game"
                icon="Media/Glyphs/Passenger.svg"
                label={t(LOC.joinGame, "Join Game")}
                disabled={modsBlocked && !inSession}
                onSelect={() => openView("join")}
            />
            <ChoiceTile
                focusKey="host-game"
                icon="Media/Glyphs/Residence.svg"
                label={t(LOC.hostGame, "Host Game")}
                disabled={inSession || modsBlocked}
                onSelect={() => openView("host")}
            />
        </ChoiceScreen>
    ) : view === "host" ? (
        <ChoiceScreen
            focusKey={PAGE_INDEX.host}
            debugName="CS2MP Host World Choice"
            initialFocused={hasSavedGame ? "load-world" : "create-world"}
            header={<ConnectionPicker />}>
            <ChoiceTile
                focusKey="load-world"
                icon="Media/Glyphs/Progress.svg"
                label={t(LOC.loadWorld, "Load World")}
                disabled={!hasSavedGame || inSession || modsBlocked}
                onSelect={() => openHostWorld("hostLoadWorld")}
            />
            <ChoiceTile
                focusKey="create-world"
                icon="Media/Glyphs/Plus.svg"
                label={t(LOC.createWorld, "Create World")}
                disabled={inSession || modsBlocked}
                onSelect={() => openHostWorld("hostCreateWorld")}
            />
        </ChoiceScreen>
    ) : joinForm;

    const pageIndex = PAGE_INDEX[view];
    const animatedPage = (
        <div key={pageIndex} style={styles.page}>
            {VanillaClassNameTransition && shrinkFadeStyles ? (
                <VanillaClassNameTransition styles={shrinkFadeStyles}>
                    <div className={childOpacityTransitionClass} style={styles.pageContent}>
                        {content}
                    </div>
                </VanillaClassNameTransition>
            ) : (
                <div className={childOpacityTransitionClass} style={styles.pageContent}>
                    {content}
                </div>
            )}
        </div>
    );

    const transitioningPage = VanillaTransitionGroup ? (
        <VanillaTransitionGroup>{animatedPage}</VanillaTransitionGroup>
    ) : animatedPage;

    const focusedPage = VanillaFocusScope ? (
        <VanillaFocusScope focused={pageIndex} activation="always">
            {transitioningPage}
        </VanillaFocusScope>
    ) : transitioningPage;

    const screenContent = <div style={styles.pageHost}>{focusedPage}</div>;

    const nativeScreen = VanillaSubScreen ? (
        <VanillaSubScreen
            focusKey={focusKey}
            className={className}
            title={title}
            onClose={backAction}>
            {screenContent}
        </VanillaSubScreen>
    ) : (
        <BackConsumer onAction={backAction}>
            <div className={className} style={styles.fallbackScreenRoot}>
                <div style={styles.fallbackHeader}>
                    <Button
                        variant="icon"
                        style={styles.fallbackBackButton}
                        onSelect={backAction}>
                        <img
                            src="Media/Glyphs/TriangleArrowLeft.svg"
                            style={styles.fallbackBackIcon}
                        />
                    </Button>
                    <div style={styles.fallbackTitle}>{title}</div>
                </div>
                <div style={styles.fallbackContent}>{screenContent}</div>
            </div>
        </BackConsumer>
    );

    return (
        <>
            {/* Keep the join overlay inside the native screen that starts the
                connection; root Menu hooks are not retained by every sub-screen. */}
            <MultiplayerJoinLoadingScreen />
            {nativeScreen}
        </>
    );
};

export const extendCreditsScreen = (CreditsScreen: any) => {
    const ExtendedCreditsScreen = (props: NativeMenuScreenProps) => {
        const multiplayerActive = useValue(multiplayerMenuActive$);
        return multiplayerActive
            ? <MultiplayerScreenRenderer {...props} />
            : <CreditsScreen {...props} />;
    };

    return ExtendedCreditsScreen;
};

const MultiplayerDisclaimer = () => {
    const { onClose } = useContext(DialogContext);

    return (
        <DisclaimerModal
            onAccept={() => {
                onClose();
                openMultiplayerScreen();
            }}
            onDecline={onClose}
        />
    );
};

export const MultiplayerMenuButton = () => {
    const { showDialog } = useContext(DialogStack);
    const t = useT();
    const accepted = useValue(disclaimerAccepted$);

    return (
        <MenuButton tinted src="Media/Glyphs/Passenger.svg"
                    onSelect={() => accepted
                        ? openMultiplayerScreen()
                        : showDialog(<MultiplayerDisclaimer />)}>
            {t(LOC.multiplayer, "Multiplayer")}
        </MenuButton>
    );
};
