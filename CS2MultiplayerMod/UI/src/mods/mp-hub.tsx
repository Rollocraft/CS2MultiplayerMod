import { GROUP, parseArray, tryModule, useT } from "mods/ui-helpers";
import { bindValue, trigger, useValue } from "cs2/api";
import { AutoNavigationScope, InputActionBarrier, NavigationDirection } from "cs2/input";
import { Button, Portal, Tooltip } from "cs2/ui";
import {
    CONNECTION_DIRECT,
    CONNECTION_LOC,
    CONNECTION_RELAY,
    ConnectionSegmented,
    JoinCodeDisplay,
    LIST_PUBLICLY_HINT,
    publicPasswordMinLength$,
    publicPasswordMissing,
    usePublicPasswordHint,
} from "mods/connection-picker";
import { RESYNC_LOC, ResyncPolicySegmented } from "mods/resync-policy";
import {
    CSSProperties,
    MouseEvent as ReactMouseEvent,
    ReactNode,
    useEffect,
    useLayoutEffect,
    useMemo,
    useRef,
    useState,
} from "react";
import { DisclaimerModal, disclaimerAccepted$ } from "mods/disclaimer";
import { FormField, FormFieldProps } from "mods/form-field";
import { HELP_PAGE, OpenHelpButton } from "mods/help-link";
import { InfoTip } from "mods/info-tip";
import { CollapsibleSection } from "mods/collapsible-section";
import { OtherModsBanner } from "mods/mods-banner";
import { TransferProgress } from "mods/transfer-progress";
import { VersionWarningBanner } from "mods/version-banner";
import { MULTIPLAYER_HEADER_HEIGHT, MULTIPLAYER_PANEL_CORNERS } from "mods/multiplayer-theme";
import { ChatEntry, groupChatMessages } from "mods/chat-grouping";
import hubClasses from "./mp-hub.module.scss";

// Keys from the mod's locales/<lang>.properties.
const LOC = {
    multiplayer: "CS2MP.UI.Multiplayer",
    sessionControls: "CS2MP.UI.SessionControls",
    sessionActivity: "CS2MP.UI.SessionActivity",
    back: "CS2MP.UI.Back",
    chatPlaceholder: "CS2MP.UI.ChatPlaceholder",
    send: "CS2MP.UI.Send",
    noMessages: "CS2MP.UI.NoMessages",
    hostSession: "CS2MP.UI.HostSession",
    lanOnly: "CS2MP.UI.LanOnly",
    ...CONNECTION_LOC,
    maxPlayers: "CS2MP.UI.MaxPlayers",
    close: "CS2MP.UI.Close",
    players: "CS2MP.UI.Players",
    host: "CS2MP.UI.Host",
    you: "CS2MP.UI.You",
    kick: "CS2MP.UI.Kick",
    confirmKick: "CS2MP.UI.ConfirmKick",
    ban: "CS2MP.UI.Ban",
    confirmBan: "CS2MP.UI.ConfirmBan",
    banHint: "CS2MP.UI.BanHint",
    cancelKick: "CS2MP.UI.CancelKick",
    tryThis: "CS2MP.UI.TryThis",
    requireApproval: "CS2MP.UI.RequireApproval",
    autoApproveSteamFriends: "CS2MP.UI.AutoApproveSteamFriends",
    settings: "CS2MP.UI.Settings",
    publicListing: "CS2MP.UI.PublicListing",
    listPublicly: "CS2MP.UI.ListPublicly",
    listPubliclyHint: "CS2MP.UI.ListPubliclyHint",
    serverName: "CS2MP.UI.ServerName",
    askJoinMessage: "CS2MP.UI.AskJoinMessage",
    simulationSync: "CS2MP.UI.SimulationSync",
    ...RESYNC_LOC,
    joinRequestTitle: "CS2MP.UI.JoinRequestTitle",
    joinRequestBody: "CS2MP.UI.JoinRequestBody",
    resyncRequestTitle: "CS2MP.UI.ResyncRequestTitle",
    resyncRequestBody: "CS2MP.UI.ResyncRequestBody",
    accept: "CS2MP.UI.Accept",
    decline: "CS2MP.UI.Decline",
    playerName: "CS2MP.UI.PlayerName",
    port: "CS2MP.UI.Port",
    password: "CS2MP.UI.Password",
    disconnect: "CS2MP.UI.Disconnect",
    closeSession: "CS2MP.UI.CloseSession",
};

// All vanilla glyphs verified to exist in Cities2_Data\Content\Game\UI\Media\Glyphs.
const ICON_MULTIPLAYER = "Media/Glyphs/Passenger.svg";
const ICON_GEAR = "Media/Glyphs/Gear.svg";
const ICON_BACK = "Media/Glyphs/ArrowLeft.svg";
const ICON_CLOSE = "Media/Glyphs/Close.svg";
const ICON_CHECK = "Media/Glyphs/Checkmark.svg";

// ---- Bindings (in addition to the ones the join dialog already uses) ---------

const chatLog$ = bindValue<string>(GROUP, "chatLog", "[]");
const inSession$ = bindValue<boolean>(GROUP, "inSession", false);
const isHost$ = bindValue<boolean>(GROUP, "isHost", false);
const localPlayerId$ = bindValue<number>(GROUP, "localPlayerId", 0);
const canHost$ = bindValue<boolean>(GROUP, "canHost", false);
const statusKind$ = bindValue<string>(GROUP, "statusKind", "offline");
const statusTitle$ = bindValue<string>(GROUP, "statusTitle", "Offline");
const statusDetail$ = bindValue<string>(GROUP, "statusDetail", "");
const statusHelp$ = bindValue<string>(GROUP, "statusHelp", "");
const statusHelpPage$ = bindValue<string>(GROUP, "statusHelpPage", "");
const progressMode$ = bindValue<string>(GROUP, "progressMode", "none");
const mapTransferPercent$ = bindValue<number>(GROUP, "mapTransferPercent", -1);
const worldSendPercent$ = bindValue<number>(GROUP, "worldSendPercent", -1);
const playerName$ = bindValue<string>(GROUP, "playerName", "Player");
const hostConnection$ = bindValue<string>(GROUP, "hostConnection", "relay");
const sessionUsesRelay$ = bindValue<boolean>(GROUP, "sessionUsesRelay", false);
// False without Steam (Microsoft Store / Game Pass): the hub drops the picker.
const relaySupported$ = bindValue<boolean>(GROUP, "relaySupported", false);
const joinCode$ = bindValue<string>(GROUP, "joinCode", "");
const hostPort$ = bindValue<string>(GROUP, "hostPort", "25001");
const hostPassword$ = bindValue<string>(GROUP, "hostPassword", "");
const maxPlayers$ = bindValue<string>(GROUP, "maxPlayers", "8");
const lanOnly$ = bindValue<boolean>(GROUP, "lanOnly", false);
const requireApproval$ = bindValue<boolean>(GROUP, "requireApproval", true);
const autoApproveSteamFriends$ = bindValue<boolean>(GROUP, "autoApproveSteamFriends", false);
const listPublicly$ = bindValue<boolean>(GROUP, "listPublicly", false);
const serverName$ = bindValue<string>(GROUP, "serverName", "");
const askJoinMessage$ = bindValue<boolean>(GROUP, "askJoinMessage", false);
const sessionListed$ = bindValue<boolean>(GROUP, "sessionListed", false);
const maxServerNameLength$ = bindValue<number>(GROUP, "maxServerNameLength", 48);
const simulationSync$ = bindValue<boolean>(GROUP, "simulationSync", true);
const resyncPolicy$ = bindValue<string>(GROUP, "resyncPolicy", "allow");
const canPingMap$ = bindValue<boolean>(GROUP, "canPingMap", false);
const cameraPlayers$ = bindValue<string>(GROUP, "cameraPlayers", "[]");
const followingPlayerId$ = bindValue<number>(GROUP, "followingPlayerId", 0);
const playerList$ = bindValue<string>(GROUP, "playerList", "[]");
const pendingJoins$ = bindValue<string>(GROUP, "pendingJoins", "[]");
const pendingResyncs$ = bindValue<string>(GROUP, "pendingResyncs", "[]");

type HubView = "chat" | "session";

const MAX_CHAT_LENGTH = 500;

interface PlayerEntry {
    id: number;
    name: string;
    isHost: boolean;
}

interface PendingJoin {
    id: number;
    name: string;
    // Sanitized C#-side, empty unless the host asked for one; shown as plain text only.
    message: string;
}

interface PendingResync {
    id: number;
    name: string;
    reason: string;
    automatic: boolean;
}

// Vanilla right-menu styling so the button is indistinguishable from the
// Chirper/notification buttons below it. The module paths are vanilla-internal
// and may move on a game update, hence the inline fallback look.

const tryClasses = (path: string): Record<string, string> | null =>
    tryModule(path, "classes");
const rmButton = tryClasses("game-ui/game/components/right-menu/right-menu-button.module.scss");
const rmMenu = tryClasses("game-ui/game/components/right-menu/right-menu.module.scss");
const dialogButton = tryClasses("game-ui/common/input/button/themes/dialog-button.module.scss");
// The info panels' text button: the game's in-game shape, hover, press and selected states.
const sectionButton = tryClasses(
    "game-ui/game/components/selected-info-panel/shared-components/primary-button/section-primary-button.module.scss");

// Once the user drags/resizes, geometry switches to measured px (see PanelGeometry).
const styles: Record<string, CSSProperties> = {
    buttonWrap: {
        position: "relative",
    },
    fallbackButton: {
        width: "43rem",
        height: "43rem",
        borderRadius: "50%",
        backgroundColor: "rgba(24, 33, 51, 0.85)",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
    },
    fallbackIcon: {
        width: "24rem",
        height: "24rem",
    },
    unreadBadge: {
        position: "absolute",
        left: "-3rem",
        top: "-3rem",
        minWidth: "16rem",
        height: "16rem",
        padding: "0 4rem",
        borderRadius: "8rem",
        backgroundColor: "#ff8a7a",
        color: "#1a2233",
        fontSize: "11rem",
        fontWeight: "bold",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        pointerEvents: "none",
    },
    panel: {
        position: "fixed",
        right: "64rem",
        bottom: "80rem",
        width: "460rem",
        // Definite height: everything inside anchors to the panel's edges, so this
        // is the one number that decides how much room the chat gets.
        height: "660rem",
        maxHeight: "calc(100vh - 100rem)",
        maxWidth: "calc(100vw - 80rem)",
        backgroundColor: "#223141",
        ...MULTIPLAYER_PANEL_CORNERS,
        boxShadow: "0 16rem 48rem rgba(0, 0, 0, 0.45)",
        zIndex: 900,
        pointerEvents: "auto",
        // Content must never paint outside the panel background — when the user
        // resizes below the natural content height, the inner areas scroll instead.
        overflow: "hidden",
    },
    header: {
        height: MULTIPLAYER_HEADER_HEIGHT,
        boxSizing: "border-box",
        display: "flex",
        alignItems: "center",
        padding: "6rem 10rem",
        backgroundColor: "#141b22",
        flexShrink: 0,
        cursor: "move",
        userSelect: "none",
    },
    headerTitle: {
        flex: 1,
        fontSize: "16rem",
        color: "#4ac0f0",
        textTransform: "uppercase",
        textAlign: "center",
        pointerEvents: "none",
    },
    headerLeading: { width: "68rem", display: "flex", alignItems: "center" },
    headerActions: { display: "flex", alignItems: "center" },
    headerButton: {
        width: "24rem",
        height: "24rem",
        marginLeft: "10rem",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
    },
    sizeIcon: { position: "relative", width: "18rem", height: "18rem", pointerEvents: "none" },
    sizeIconCorner: { position: "absolute", width: "6rem", height: "6rem", boxSizing: "border-box" },
    // Anchored, not distributed: a column flex child does not reliably take the
    // panel's leftover height in the game's UI runtime, which left the action
    // buttons floating with dead space under them. Both insets are definite, so
    // this box is exactly the panel minus its header (see PanelBody).
    body: {
        position: "absolute",
        top: MULTIPLAYER_HEADER_HEIGHT,
        left: 0,
        right: 0,
        bottom: 0,
        overflow: "hidden",
    },
    // Natural height at the panel's top edge. No "overflow: hidden" here: it reads
    // back as a zero height, which put the chat on top of the player list. The
    // padding is what keeps a last child's bottom margin inside the measured box.
    bodyTop: {
        position: "absolute",
        top: "12rem",
        left: "14rem",
        right: "14rem",
        paddingBottom: "1rem",
    },
    // Everything between the two blocks; its insets are computed from them.
    bodyMiddle: {
        position: "absolute",
        left: "14rem",
        right: "14rem",
    },
    // Natural height at the panel's bottom edge - where the buttons live.
    bodyBottom: {
        position: "absolute",
        bottom: "12rem",
        left: "14rem",
        right: "14rem",
    },
    // Fields live in here so a small panel scrolls them while the footer
    // (action buttons) stays pinned to the panel bottom.
    scrollArea: {
        height: "100%",
        overflowY: "auto",
    },
    // Fills the whole middle block, which is what is left of the panel once the
    // player list and the buttons have taken their own height.
    chatList: {
        height: "100%",
        boxSizing: "border-box",
        overflowY: "auto",
        backgroundColor: "rgba(0, 0, 0, 0.16)",
        ...MULTIPLAYER_PANEL_CORNERS,
        padding: "14rem",
    },
    chatEmpty: {
        fontSize: "13rem",
        color: "#9bb3c2",
        textAlign: "center",
        padding: "24rem 12rem",
        lineHeight: "1.5",
    },
    chatLine: {
        marginBottom: "12rem",
        display: "flex",
        flexDirection: "column",
        alignItems: "flex-start",
        wordBreak: "break-word",
    },
    chatMeta: {
        display: "flex",
        alignItems: "center",
        marginBottom: "5rem",
        padding: "0 3rem",
        maxWidth: "100%",
    },
    chatBubble: {
        maxWidth: "100%",
        boxSizing: "border-box",
        padding: "9rem 12rem",
        backgroundColor: "#2e4356",
        borderRadius: "10rem",
        color: "#f0fbff",
        fontSize: "14rem",
        lineHeight: "1.4",
        whiteSpace: "pre-wrap",
        wordBreak: "break-word",
        marginBottom: "4rem",
    },
    chatBubbleLocal: {
        backgroundColor: "#224f65",
    },
    chatTime: {
        color: "#9bb3c2",
        fontSize: "11rem",
        marginLeft: "8rem",
    },
    chatSender: {
        color: "#99d3e9",
        fontSize: "12rem",
        fontWeight: "bold",
        overflow: "hidden",
        textOverflow: "ellipsis",
        whiteSpace: "nowrap",
    },
    systemLine: {
        display: "flex",
        alignItems: "flex-start",
        fontSize: "13rem",
        lineHeight: "1.4",
        color: "#c5d5df",
        padding: "8rem 0",
        wordBreak: "break-word",
    },
    activityTime: {
        width: "44rem",
        flexShrink: 0,
        marginRight: "14rem",
        color: "#9bb3c2",
        fontSize: "12rem",
        whiteSpace: "nowrap",
    },
    activityText: { flex: 1, minWidth: 0 },
    inputRow: {
        display: "flex",
        alignItems: "center",
        width: "100%",
        flexShrink: 0,
    },
    chatInput: {
        flex: 1,
        width: "100%",
        minWidth: 0,
        boxSizing: "border-box",
        height: "38rem",
        fontSize: "14rem",
        color: "#ffffff",
        backgroundColor: "rgba(0, 0, 0, 0.35)",
        border: "1rem solid rgba(157, 193, 222, 0.35)",
        borderRadius: "3rem",
        padding: "8rem 10rem",
    },
    sendButton: {
        marginLeft: "8rem",
        height: "38rem",
        minWidth: "72rem",
    },
    footer: {
        display: "flex",
        justifyContent: "flex-end",
        flexShrink: 0,
    },
    footerButton: {
        marginLeft: "10rem",
        padding: "7rem 16rem",
    },
    actionButton: {
        minHeight: "34rem",
        padding: "7rem 12rem",
        fontSize: "13rem",
        fontFamily: "var(--fontFamily)",
        fontWeight: "normal",
        fontStyle: "normal",
        letterSpacing: "normal",
        lineHeight: "1.2",
        textAlign: "center",
        textTransform: "none",
    },
    sessionFooter: {
        display: "flex",
        marginTop: "10rem",
    },
    sessionFooterButton: { flex: "2 1 0", minWidth: 0 },
    sessionSecondaryButton: { flex: "1 1 0", minWidth: 0, marginLeft: "8rem" },
    sessionActionButton: { width: "100%", minHeight: "40rem" },
    sectionContent: { padding: "4rem 12rem 8rem" },
    toolHint: { color: "#9bb3c2", fontSize: "12rem", lineHeight: "1.4", marginBottom: "6rem" },
    composer: { position: "relative", display: "flex", flex: 1, minWidth: 0 },
    composerRemaining: {
        position: "absolute",
        top: 0,
        bottom: 0,
        right: "10rem",
        display: "flex",
        alignItems: "center",
        color: "#ffcf87",
        fontSize: "11rem",
        whiteSpace: "nowrap",
        pointerEvents: "none",
    },
    hint: {
        fontSize: "12.5rem",
        color: "rgba(255, 255, 255, 0.55)",
        margin: "2rem 0 12rem 0",
    },
    hintWarning: {
        fontSize: "12.5rem",
        color: "#ffb454",
        margin: "-4rem 0 10rem 0",
    },
    errorLine: {
        fontSize: "13rem",
        color: "#ffd7d1",
        backgroundColor: "rgba(205, 82, 70, 0.18)",
        borderLeft: "3rem solid #ff8a7a",
        ...MULTIPLAYER_PANEL_CORNERS,
        padding: "9rem 10rem",
        margin: "4rem 0 10rem 0",
        wordBreak: "break-word",
    },
    errorTitle: {
        fontWeight: "bold",
        marginBottom: "4rem",
    },
    errorHelpTitle: {
        marginTop: "8rem",
        color: "#9dc1de",
        fontSize: "11.5rem",
        fontWeight: "bold",
        textTransform: "uppercase",
    },
    errorHelp: {
        marginTop: "3rem",
        color: "#d6e2eb",
        lineHeight: "1.35",
    },
    errorHelpButton: {
        marginTop: "7rem",
        padding: "4rem 10rem",
        fontSize: "11.5rem",
    },
    row: {
        display: "flex",
        alignItems: "center",
        marginBottom: "10rem",
    },
    // One label column: text wraps inside it, so every control starts on the same line.
    label: {
        width: "150rem",
        paddingRight: "10rem",
        fontSize: "13.5rem",
        color: "#9dc1de",
        textTransform: "uppercase",
        flexShrink: 0,
        display: "flex",
        alignItems: "center",
    },
    input: {
        flex: 1,
        fontSize: "14rem",
        color: "#ffffff",
        backgroundColor: "rgba(0, 0, 0, 0.35)",
        border: "1rem solid rgba(157, 193, 222, 0.35)",
        borderRadius: "3rem",
        padding: "5rem 10rem",
    },
    inputDisabled: {
        opacity: 0.55,
        cursor: "not-allowed",
    },
    toggleBox: {
        width: "22rem",
        height: "22rem",
        borderRadius: "3rem",
        backgroundColor: "rgba(0, 0, 0, 0.35)",
        border: "1rem solid rgba(157, 193, 222, 0.35)",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
    },
    toggleCheck: {
        width: "14rem",
        height: "14rem",
        // Checkmark.svg carries no fill of its own, so it paints black; normalize
        // it to white the way the game's tinted icons do.
        filter: "brightness(0) invert(1)",
    },
    resizeHandle: {
        position: "absolute",
        right: 0,
        bottom: 0,
        width: "18rem",
        height: "18rem",
    },
    resizeGrip: {
        position: "absolute",
        right: "3rem",
        bottom: "3rem",
        width: 0,
        height: 0,
        borderBottom: "11rem solid rgba(157, 193, 222, 0.45)",
        borderLeft: "11rem solid transparent",
    },
    activityDetail: {
        marginTop: "-9rem",
        marginBottom: "10rem",
        color: "rgba(255, 255, 255, 0.68)",
        fontSize: "12rem",
        lineHeight: "1.3",
    },
    outlinedSection: {
        position: "relative",
        flexShrink: 0,
        marginTop: "8rem",
        marginBottom: "10rem",
        paddingTop: "8rem",
        border: "1rem solid rgba(153, 211, 233, 0.3)",
        ...MULTIPLAYER_PANEL_CORNERS,
    },
    sectionLegend: {
        position: "absolute",
        top: "-9rem",
        left: "50%",
        transform: "translateX(-50%)",
        padding: "0 12rem",
        backgroundColor: "#223141",
        color: "#4ac0f0",
        fontSize: "13rem",
        lineHeight: "18rem",
        whiteSpace: "nowrap",
    },
    playerList: {
        ...MULTIPLAYER_PANEL_CORNERS,
        maxHeight: "160rem",
        overflowY: "auto",
    },
    playerRow: {
        padding: "12rem",
        display: "flex",
        flexDirection: "column",
        borderBottom: "1rem solid rgba(157, 193, 222, 0.14)",
    },
    playerIdentity: { display: "flex", alignItems: "center", width: "100%" },
    playerActions: { display: "flex", flexWrap: "wrap", marginTop: "8rem", width: "100%" },
    playerAction: { marginRight: "6rem", marginTop: "4rem", fontSize: "12rem" },
    playerName: {
        flexGrow: 1,
        minWidth: 0,
        color: "#ffffff",
        fontSize: "13.5rem",
        overflow: "hidden",
        textOverflow: "ellipsis",
        whiteSpace: "nowrap",
    },
    playerBadge: {
        marginLeft: "6rem",
        color: "#99d3e9",
        backgroundColor: "rgba(74, 192, 240, 0.1)",
        borderRadius: "4rem",
        padding: "3rem 6rem",
        fontSize: "10.5rem",
        textTransform: "uppercase",
    },
    // Join-request prompt: floats at the top of the screen so the host notices it
    // without being locked out of the game (only the cards capture input).
    joinAnchor: {
        position: "fixed",
        top: "24rem",
        left: 0,
        right: 0,
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        zIndex: 10002,
        pointerEvents: "none",
    },
    joinCard: {
        width: "440rem",
        maxWidth: "90%",
        backgroundColor: "rgba(24, 33, 51, 0.98)",
        border: "1rem solid rgba(157, 193, 222, 0.3)",
        borderLeft: "4rem solid #72c8f0",
        ...MULTIPLAYER_PANEL_CORNERS,
        padding: "16rem 18rem",
        marginBottom: "10rem",
        boxShadow: "0 12rem 36rem rgba(0, 0, 0, 0.5)",
        pointerEvents: "auto",
    },
    joinCardTitle: {
        fontSize: "12.5rem",
        color: "#9dc1de",
        textTransform: "uppercase",
        letterSpacing: "1rem",
        marginBottom: "8rem",
    },
    joinCardBody: {
        fontSize: "15rem",
        color: "#ffffff",
        marginBottom: "14rem",
        wordBreak: "break-word",
    },
    joinCardMessage: {
        fontSize: "14rem",
        lineHeight: "1.35",
        fontStyle: "italic",
        color: "#dbe9f5",
        backgroundColor: "rgba(0, 0, 0, 0.25)",
        borderLeft: "2rem solid rgba(157, 193, 222, 0.5)",
        padding: "8rem 10rem",
        marginTop: "-6rem",
        marginBottom: "14rem",
        maxHeight: "100rem",
        overflow: "hidden",
        wordBreak: "break-word",
    },
    joinCardButtons: {
        display: "flex",
        justifyContent: "flex-end",
    },
    joinCardButton: {
        marginLeft: "10rem",
        padding: "7rem 18rem",
    },
};

// ---- Panel body layout ----------------------------------------------------------

const blockHeight = (element: HTMLDivElement | null): number => {
    if (element === null) return 0;
    const box = element.offsetHeight;
    const content = element.scrollHeight;
    return box > content ? box : content;
};

/**
 * A panel view laid out against the panel's own edges: the top and bottom blocks
 * keep their natural height where they are pinned, and the middle block takes
 * exactly what is left. The blocks are measured because their height is content
 * (a player list grows, a transfer bar comes and goes) - nothing here relies on
 * the runtime handing a flex child the container's leftover space, which is what
 * previously stranded the buttons mid-panel.
 */
const PanelBody = ({ top, middle, bottom }: {
    top?: ReactNode;
    middle: ReactNode;
    bottom?: ReactNode;
}) => {
    const topRef = useRef<HTMLDivElement | null>(null);
    const bottomRef = useRef<HTMLDivElement | null>(null);
    const [topHeight, setTopHeight] = useState(0);
    const [bottomHeight, setBottomHeight] = useState(0);

    // Re-measured after every render AND on the following frames: this runtime
    // lays out asynchronously, so the read right after the commit still answers 0
    // on a freshly opened panel — which is what put the chat over the player list
    // until some unrelated re-render happened to measure again. A zero for a block
    // that has content is kept out, and identical values do not re-render, so this
    // settles within a frame or two of opening.
    useLayoutEffect(() => {
        const measure = () => {
            const measuredTop = blockHeight(topRef.current);
            const measuredBottom = blockHeight(bottomRef.current);
            if (measuredTop > 0 || !top) setTopHeight(measuredTop);
            if (measuredBottom > 0 || !bottom) setBottomHeight(measuredBottom);
        };

        measure();
        let frame = requestAnimationFrame(function settle() {
            measure();
            frame = requestAnimationFrame(measure);
        });
        return () => cancelAnimationFrame(frame);
    });

    const middleStyle: CSSProperties = {
        ...styles.bodyMiddle,
        // 12rem is the panel's own inset; the extra 10rem is the gap to the buttons.
        top: top ? `calc(12rem + ${topHeight}px)` : "12rem",
        bottom: bottom ? `calc(22rem + ${bottomHeight}px)` : "12rem",
    };

    return (
        <div style={styles.body}>
            {top ? <div ref={topRef} style={styles.bodyTop}>{top}</div> : null}
            <div style={middleStyle}>{middle}</div>
            {bottom ? <div ref={bottomRef} style={styles.bodyBottom}>{bottom}</div> : null}
        </div>
    );
};

// ---- Form building blocks -----------------------------------------------------

// In game nearly every letter is a shortcut (B = bulldozer), so a focused field holds an
// InputActionBarrier.
const HubField = (props: FormFieldProps) => (
    <FormField
        {...props}
        styles={styles}
        wrap={(input, editing) => <InputActionBarrier disabled={!editing}>{input}</InputActionBarrier>}
    />
);

const HubCheckBox = ({ value, disabled, onChange }: {
    value: boolean;
    disabled?: boolean;
    onChange: (value: boolean) => void;
}) => (
    <div
        className={[hubClasses.toggle, disabled ? hubClasses.disabled : hubClasses.enabled].join(" ")}
        style={disabled ? { ...styles.toggleBox, ...styles.inputDisabled } : styles.toggleBox}
        onClick={() => {
            if (!disabled) onChange(!value);
        }}>
        {value ? <img src={ICON_CHECK} style={styles.toggleCheck} /> : null}
    </div>
);

const HubToggle = ({ label, value, disabled, info, onChange }: {
    label: string;
    value: boolean;
    disabled?: boolean;
    info?: string;
    onChange: (value: boolean) => void;
}) => (
    <div style={styles.row}>
        <div style={styles.label}>
            <span>{label}</span>
            {info ? <InfoTip text={info} /> : null}
        </div>
        <HubCheckBox value={value} disabled={disabled} onChange={onChange} />
    </div>
);

const PanelSizeIcon = ({ minimized }: { minimized: boolean }) => (
    <span aria-hidden="true" style={styles.sizeIcon}>
        <span style={{
            ...styles.sizeIconCorner, top: "2rem", right: "2rem",
            ...(minimized
                ? { borderTop: "2rem solid #ffffff", borderRight: "2rem solid #ffffff" }
                : { borderBottom: "2rem solid #ffffff", borderLeft: "2rem solid #ffffff" }),
        }} />
        <span style={{
            ...styles.sizeIconCorner, bottom: "2rem", left: "2rem",
            ...(minimized
                ? { borderBottom: "2rem solid #ffffff", borderLeft: "2rem solid #ffffff" }
                : { borderTop: "2rem solid #ffffff", borderRight: "2rem solid #ffffff" }),
        }} />
    </span>
);

const HeaderIconButton = ({ src, children, tooltip, selected, disabled, onSelect }: {
    src?: string;
    children?: ReactNode;
    tooltip: string;
    selected?: boolean;
    disabled?: boolean;
    onSelect: () => void;
}) => {
    const buttonStyle = {
        ...styles.headerButton,
        color: selected ? "#72c8f0" : "#ffffff",
        "--iconColor": selected ? "#72c8f0" : "#ffffff",
        "--iconSize": "18rem",
        "--iconWidth": "18rem",
        "--iconHeight": "18rem",
    } as CSSProperties;

    return (
        <Tooltip tooltip={tooltip} direction="down">
            {/* stopPropagation: header mousedown starts the panel drag */}
            <div
                onMouseDown={(e) => e.stopPropagation()}>
                <Button
                    variant="round"
                    {...(src ? { src, tinted: true } : {})}
                    selected={selected}
                    disabled={disabled}
                    style={buttonStyle}
                    onSelect={onSelect}
                >{children}</Button>
            </div>
        </Tooltip>
    );
};

// The game's own in-game button themes: the info panels' dark button for normal actions, green for
// confirming ones, the red dialog button for destructive ones. Shape and hover come from the theme.
const HubButton = ({ tone, hint, style, theme, ...props }: Parameters<typeof Button>[0] & {
    tone?: "normal" | "primary" | "success" | "danger";
    hint?: string;
}) => {
    const appearance = tone || (props.variant === "primary" ? "primary" : "normal");
    const confirming = appearance === "primary" || appearance === "success";
    const resolvedTheme = appearance === "danger" && dialogButton
        ? { button: `${dialogButton.button} ${dialogButton.negative}` }
        : theme ?? (appearance === "normal" && sectionButton ? { button: sectionButton.button } : undefined);
    const className = [
        props.disabled ? hubClasses.disabled : hubClasses.enabled, props.className].filter(Boolean).join(" ");
    const button = (
        <Button
            {...props}
            // Native Button spreads its props over its default theme. Omit the
            // property entirely when no override exists; theme={undefined} erases it.
            {...(resolvedTheme ? { theme: resolvedTheme } : {})}
            variant={confirming ? "primary" : "flat"}
            className={className}
            style={{
                ...styles.actionButton,
                opacity: props.disabled ? 0.45 : 1,
                ...style,
            }}
        />
    );
    return hint ? <Tooltip tooltip={hint} direction="down">{button}</Tooltip> : button;
};

// The host/session settings fields. Connection-defining fields are locked while
// a session runs (the running server cannot re-bind them).
const SettingsFields = () => {
    const t = useT();
    const inSession = useValue(inSession$);
    const playerName = useValue(playerName$);
    const hostPort = useValue(hostPort$);
    const hostPassword = useValue(hostPassword$);
    const maxPlayers = useValue(maxPlayers$);
    const lanOnly = useValue(lanOnly$);
    const requireApproval = useValue(requireApproval$);
    const autoApproveSteamFriends = useValue(autoApproveSteamFriends$);
    const simulationSync = useValue(simulationSync$);
    const resyncPolicy = useValue(resyncPolicy$);
    const hostConnection = useValue(hostConnection$);
    const sessionUsesRelay = useValue(sessionUsesRelay$);
    const relaySupported = useValue(relaySupported$);
    const joinCode = useValue(joinCode$);
    const listPublicly = useValue(listPublicly$);
    const serverName = useValue(serverName$);
    const askJoinMessage = useValue(askJoinMessage$);
    const sessionListed = useValue(sessionListed$);
    const maxServerNameLength = useValue(maxServerNameLength$);

    // In a live session follow what it actually runs on; outside one, what is
    // configured for the next.
    const relay = inSession ? sessionUsesRelay : relaySupported && hostConnection !== CONNECTION_DIRECT;
    const minPasswordLength = useValue(publicPasswordMinLength$);
    const passwordHint = usePublicPasswordHint(hostPassword, minPasswordLength);
    const passwordMissing = !inSession &&
        publicPasswordMissing(relay, lanOnly, hostPassword, minPasswordLength);
    const listed = inSession ? sessionListed : relaySupported && listPublicly;
    const [settingsOpen, setSettingsOpen] = useState(false);
    const [listingOpen, setListingOpen] = useState(false);

    return (
        <>
            <HubField
                label={t(LOC.playerName, "Player Name")}
                value={playerName}
                disabled={inSession}
                onChange={(v) => trigger(GROUP, "setPlayerName", v)}
            />
            {relaySupported && (
                <div style={styles.row}>
                    <div style={styles.label}>{t(LOC.mode, "Connection")}</div>
                    <ConnectionSegmented
                        value={relay ? CONNECTION_RELAY : CONNECTION_DIRECT}
                        disabled={inSession}
                        onChange={(v) => trigger(GROUP, "setHostConnection", v)}
                    />
                </div>
            )}
            {/* A relay session has no port to show; the code is what a host passes on.
                Read-only and select-on-click - the game exposes no clipboard API. */}
            {relay ? (
                <div style={styles.row}>
                    <div style={styles.label}>{t(LOC.joinCode, "Join Code")}</div>
                    <JoinCodeDisplay code={joinCode} style={styles.input} />
                </div>
            ) : (
                <HubField
                    label={t(LOC.port, "Port")}
                    value={hostPort}
                    disabled={inSession}
                    onChange={(v) => trigger(GROUP, "setHostPort", v)}
                />
            )}
            <HubField
                label={t(LOC.password, "Password")}
                secret
                value={hostPassword}
                disabled={inSession}
                onChange={(v) => trigger(GROUP, "setHostPassword", v)}
            />
            {passwordMissing ? <div style={styles.hintWarning}>{passwordHint}</div> : null}
            <HubField
                label={t(LOC.maxPlayers, "Max Players")}
                value={maxPlayers}
                disabled={inSession}
                onChange={(v) => trigger(GROUP, "setMaxPlayers", v)}
            />
            {/* Nothing on this machine is reachable over a relay, so there is no
                exposure for the LAN filter to narrow. */}
            {!relay && (
                <HubToggle
                    label={t(LOC.lanOnly, "LAN Only")}
                    value={lanOnly}
                    disabled={inSession}
                    onChange={(v) => trigger(GROUP, "setLanOnly", v)}
                />
            )}
            <CollapsibleSection
                compact
                title={t(LOC.settings, "Settings")}
                open={settingsOpen}
                onToggle={() => setSettingsOpen(!settingsOpen)}>
                {/* A listed session is open to strangers, so approval is fixed on. */}
                <HubToggle
                    label={t(LOC.requireApproval, "Approve Players")}
                    value={requireApproval || listed}
                    disabled={inSession || listed}
                    onChange={(v) => trigger(GROUP, "setRequireApproval", v)}
                />
                {(relay || listed) && (
                    <HubToggle
                        label={t(LOC.autoApproveSteamFriends, "Auto-Approve Steam Friends")}
                        value={autoApproveSteamFriends}
                        disabled={inSession || !(requireApproval || listed)}
                        onChange={(v) => trigger(GROUP, "setAutoApproveSteamFriends", v)}
                    />
                )}
                <div style={styles.row}>
                    <div style={styles.label}>{t(LOC.policy, "Client Resync Requests")}</div>
                    <ResyncPolicySegmented
                        value={resyncPolicy}
                        disabled={inSession}
                        onChange={(v) => trigger(GROUP, "setResyncPolicy", v)}
                    />
                </div>
                {/* The host answers for the whole session, so this is fixed once one is
                    running - a client sees the host's answer, not its own. */}
                <HubToggle
                    label={t(LOC.simulationSync, "Simulation Sync")}
                    value={simulationSync}
                    disabled={inSession}
                    onChange={(v) => trigger(GROUP, "setSimulationSync", v)}
                />
            </CollapsibleSection>
            {relaySupported && (
                <CollapsibleSection
                    compact
                    title={t(LOC.publicListing, "Public Listing")}
                    open={listingOpen}
                    onToggle={() => setListingOpen(!listingOpen)}>
                    <HubToggle
                        label={t(LOC.listPublicly, "List Publicly")}
                        value={listed}
                        disabled={inSession}
                        info={t(LOC.listPubliclyHint, LIST_PUBLICLY_HINT)}
                        onChange={(v) => trigger(GROUP, "setListPublicly", v)}
                    />
                    {/* Shown even while listing is off, greyed out, so the choice is visible up front. */}
                    <HubToggle
                        label={t(LOC.askJoinMessage, "Require Join Message")}
                        value={askJoinMessage}
                        disabled={inSession || !listed}
                        onChange={(v) => trigger(GROUP, "setAskJoinMessage", v)}
                    />
                    <HubField
                        label={t(LOC.serverName, "Lobby Name")}
                        value={serverName}
                        disabled={inSession || !listed}
                        maxLength={maxServerNameLength}
                        onChange={(v) => trigger(GROUP, "setServerName", v)}
                    />
                </CollapsibleSection>
            )}
        </>
    );
};

// ---- Panel views ----------------------------------------------------------------

// No session: the host setup IS the main view, so the settings are always
// visible here. No status header — a failed host/connect shows as a short
// error line above the action button instead.
const HostSetupView = () => {
    const t = useT();
    const canHost = useValue(canHost$);
    const statusKind = useValue(statusKind$);
    const statusTitle = useValue(statusTitle$);
    const statusDetail = useValue(statusDetail$);
    const statusHelp = useValue(statusHelp$);
    const statusHelpPage = useValue(statusHelpPage$);

    return (
        <PanelBody
            middle={
                <div style={styles.scrollArea}>
                    {/* Explains the disabled Host button: canHost is false C#-side while
                        any other mod is live. */}
                    <OtherModsBanner />
                    <VersionWarningBanner />
                    <SettingsFields />
                    {statusKind === "error" ? (
                        <div style={styles.errorLine}>
                            <div style={styles.errorTitle}>{statusTitle}</div>
                            {statusDetail ? <div>{statusDetail}</div> : null}
                            {statusHelp ? (
                                <>
                                    <div style={styles.errorHelpTitle}>{t(LOC.tryThis, "Try this")}</div>
                                    <div style={styles.errorHelp}>{statusHelp}</div>
                                </>
                            ) : null}
                            <OpenHelpButton
                                page={statusHelpPage || HELP_PAGE.errors}
                                style={styles.errorHelpButton}
                            />
                        </div>
                    ) : null}
                </div>
            }
            bottom={
                <div style={styles.footer}>
                    <HubButton
                        tone="success"
                        style={styles.footerButton}
                        disabled={!canHost}
                        onSelect={() => {
                            if (canHost) trigger(GROUP, "hostStart");
                        }}>
                        {t(LOC.hostSession, "Host Session")}
                    </HubButton>
                </div>
            }
        />
    );
};

const OutlinedSection = ({ title, children, style }: {
    title: string;
    children: ReactNode;
    style?: CSSProperties;
}) => {
    return (
        <div style={{ ...styles.outlinedSection, ...style }}>
            <div style={styles.sectionLegend}>{title}</div>
            {children}
        </div>
    );
};

const PlayerList = ({ players }: { players: PlayerEntry[] }) => {
    const t = useT();
    const isHost = useValue(isHost$);
    const localName = useValue(playerName$);
    const localPlayerId = useValue(localPlayerId$);
    const cameraJson = useValue(cameraPlayers$);
    const following = useValue(followingPlayerId$);
    const cameras = useMemo(() => parseArray<Pick<PlayerEntry, "id" | "name">>(cameraJson), [cameraJson]);
    const roster = isHost
        ? players.map(player => ({ ...player, isLocal: player.isHost }))
        : [
            { id: localPlayerId, name: localName, isHost: false, isLocal: true },
            ...cameras.map(player => ({ ...player, isHost: player.id === 1, isLocal: false })),
        ];
    const [pendingAction, setPendingAction] = useState<{
        playerId: number;
        action: "kick" | "ban";
    } | null>(null);

    useEffect(() => {
        if (pendingAction !== null &&
            !roster.some((player) => player.id === pendingAction.playerId))
            setPendingAction(null);
    }, [roster, pendingAction]);

    return (
        <OutlinedSection title={t(LOC.players, "Players")}>
            <div style={styles.playerList}>
                {roster.map((player, index) => {
                    const pendingKind = pendingAction !== null &&
                        pendingAction.playerId === player.id
                        ? pendingAction.action
                        : null;
                    const confirming = pendingKind !== null;
                    return (
                        <div key={player.id} style={{ ...styles.playerRow, ...(index === roster.length - 1 ? { borderBottom: "none" } : {}) }}>
                            <div style={styles.playerIdentity}>
                                <div style={styles.playerName}>{player.name}</div>
                                {player.isHost ? <span style={styles.playerBadge}>{t(LOC.host, "Host")}</span> : null}
                                {player.isLocal ? <span style={styles.playerBadge}>{t(LOC.you, "You")}</span> : null}
                            </div>
                            {!player.isLocal ? (
                                <div style={styles.playerActions}>
                                    <HubButton
                                        style={styles.playerAction}
                                        disabled={!cameras.some(camera => camera.id === player.id)}
                                        hint={t("CS2MP.UI.ViewPlayerHint", "Move your camera to this player's location.")}
                                        onSelect={() => trigger(GROUP, "goToPlayer", player.id)}>
                                        {t("CS2MP.UI.ViewPlayer", "View location")}
                                    </HubButton>
                                    <HubButton
                                        style={styles.playerAction}
                                        selected={following === player.id}
                                        disabled={following !== player.id && !cameras.some(camera => camera.id === player.id)}
                                        hint={t("CS2MP.UI.FollowHint", "Move the camera or press Escape to stop following.")}
                                        onSelect={() => following === player.id
                                            ? trigger(GROUP, "stopFollowing")
                                            : trigger(GROUP, "followPlayer", player.id)}>
                                        {following === player.id
                                            ? t("CS2MP.UI.StopFollowing", "Stop following")
                                            : t("CS2MP.UI.FollowPlayer", "Follow")}
                                    </HubButton>
                                    {isHost && !confirming ? <>
                                        <HubButton
                                            style={styles.playerAction}
                                            hint={t("CS2MP.UI.RemovePlayerHint", "Disconnect this player. They can ask to join again.")}
                                            onSelect={() => setPendingAction({ playerId: player.id, action: "kick" })}>
                                            {t("CS2MP.UI.RemovePlayer", "Remove")}
                                        </HubButton>
                                        <HubButton
                                            tone="danger"
                                            style={styles.playerAction}
                                            hint={t(LOC.banHint, "Remove this player and prevent them from rejoining until hosting stops.")}
                                            onSelect={() => setPendingAction({ playerId: player.id, action: "ban" })}>
                                            {t("CS2MP.UI.BlockPlayer", "Block rejoining")}
                                        </HubButton>
                                    </> : null}
                                </div>
                            ) : null}
                            {confirming ? (
                                <>
                                    <div style={{ ...styles.toolHint, marginTop: "10rem" }}>
                                        {t(pendingKind === "ban" ? "CS2MP.UI.BlockPlayerConfirm" : "CS2MP.UI.RemovePlayerConfirm",
                                            pendingKind === "ban" ? "Block {0} from rejoining this session?" : "Remove {0} from the session?")
                                            .replace("{0}", player.name)}
                                    </div>
                                    <div style={styles.playerActions}>
                                    <HubButton
                                        tone="danger"
                                        style={styles.playerAction}
                                        onSelect={() => {
                                            trigger(
                                                GROUP,
                                                pendingKind === "ban"
                                                    ? "banPlayer"
                                                    : "kickPlayer",
                                                player.id,
                                            );
                                            setPendingAction(null);
                                        }}>
                                        {t("CS2MP.UI.Confirm", "Confirm")}
                                    </HubButton>
                                    <HubButton
                                        style={styles.playerAction}
                                        onSelect={() => setPendingAction(null)}>
                                        {t(LOC.cancelKick, "Cancel")}
                                    </HubButton>
                                    </div>
                                </>
                            ) : null}
                        </div>
                    );
                })}
            </div>
        </OutlinedSection>
    );
};


const SessionView = ({ entries, players, view, compact }: {
    entries: ChatEntry[];
    players: PlayerEntry[];
    view: HubView;
    compact: boolean;
}) => {
    const t = useT();
    const canPingMap = useValue(canPingMap$);
    const mapTransferPercent = useValue(mapTransferPercent$);
    const worldSendPercent = useValue(worldSendPercent$);
    const isHost = useValue(isHost$);
    const progressMode = useValue(progressMode$);
    const statusTitle = useValue(statusTitle$);
    const statusDetail = useValue(statusDetail$);
    const [draft, setDraft] = useState("");
    const [typing, setTyping] = useState(false);
    const remaining = MAX_CHAT_LENGTH - draft.length;
    const showRemaining = remaining <= 100;
    const listRef = useRef<HTMLDivElement | null>(null);
    // The panel unmounts this view when it closes, so "first pass after mount" is
    // exactly "the player just opened the chat".
    const openedRef = useRef(true);
    const lastCompactRef = useRef(compact);
    const scrollSettlesAtRef = useRef(0);
    const stickToBottomRef = useRef(true);
    const messages = useMemo(() => entries.filter(entry => entry.kind === "message"), [entries]);
    const messageGroups = useMemo(() => groupChatMessages(messages), [messages]);
    const activity = useMemo(() => entries.filter(entry => entry.kind !== "message"), [entries]);
    const latestMessageId = messages.length > 0 ? messages[messages.length - 1].id : 0;

    // Keep the newest line in view (only auto-stick when already near the bottom,
    // so scrolling back through history is not yanked away by new messages).
    // Opening the panel always lands on the newest line, not on the oldest one.
    useEffect(() => {
        const changedMode = lastCompactRef.current !== compact;
        lastCompactRef.current = compact;
        const el = listRef.current;
        if (!el) { openedRef.current = true; return; }
        if (openedRef.current || (changedMode && stickToBottomRef.current)) {
            openedRef.current = false;
            scrollSettlesAtRef.current = performance.now() + 300;
        }
        if (performance.now() < scrollSettlesAtRef.current && stickToBottomRef.current) {
            el.scrollTop = el.scrollHeight;
            // Repeated over the next frames: the panel's own geometry is still
            // settling on this one, and the list shrinking afterwards would leave
            // this first jump short of the newest line.
            let frame = requestAnimationFrame(function toNewest() {
                const list = listRef.current;
                if (list && stickToBottomRef.current) list.scrollTop = list.scrollHeight;
                if (performance.now() < scrollSettlesAtRef.current)
                    frame = requestAnimationFrame(toNewest);
            });
            return () => cancelAnimationFrame(frame);
        }
        if (stickToBottomRef.current) {
            const frame = requestAnimationFrame(() => { el.scrollTop = el.scrollHeight; });
            return () => cancelAnimationFrame(frame);
        }
    }, [latestMessageId, view, compact]);

    const send = () => {
        const text = draft.trim();
        if (!text) return;
        trigger(GROUP, "sendChat", text);
        setDraft("");
    };

    const activityPercent = isHost ? worldSendPercent : mapTransferPercent;
    const showActivity = progressMode !== "none";

    const topBlock = (
        <>
            {view === "chat" ? <PlayerList players={players} /> : null}
            {showActivity ? (
                <>
                    <TransferProgress
                        percent={activityPercent}
                        label={statusTitle}
                        indeterminate={progressMode === "indeterminate"}
                    />
                    {statusDetail ? <div style={styles.activityDetail}>{statusDetail}</div> : null}
                </>
            ) : null}
        </>
    );

    const chatFeed = (
        <div ref={listRef} style={{
            ...styles.chatList,
            backgroundColor: compact ? "rgba(0, 0, 0, 0.08)" : "rgba(0, 0, 0, 0.16)",
            padding: compact ? "10rem" : "14rem",
            transition: "background-color 180ms ease-out",
        }} onScroll={() => {
            const el = listRef.current;
            if (el) stickToBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 60;
        }}>
            {messages.length === 0 ? (
                <div style={styles.chatEmpty}>{t(LOC.noMessages, "No messages yet.")}</div>
            ) : (
                messageGroups.map(group => (
                    <div key={group.entries[0].id} style={{ ...styles.chatLine, marginBottom: compact ? "8rem" : "12rem", alignItems: group.entries[0].isLocal ? "flex-end" : "flex-start" }}>
                        <div style={styles.chatMeta}>
                            <span style={styles.chatSender}>{group.entries[0].isLocal ? t(LOC.you, "You") : group.entries[0].sender}</span>
                            <span style={styles.chatTime}>{group.entries[0].time}</span>
                        </div>
                        {group.entries.map(entry => <div key={entry.id} style={{
                            ...styles.chatBubble,
                            ...(entry.isLocal ? styles.chatBubbleLocal : {}),
                            ...(compact ? { padding: "6rem 9rem", fontSize: "13rem" } : {}),
                        }}>{entry.text}</div>)}
                    </div>
                ))
            )}
        </div>
    );

    const bottomBlock = (
        <>
            <div style={styles.inputRow}>
                <div style={styles.composer}>
                    <InputActionBarrier disabled={!typing}>
                        <input
                            type="text"
                            className={hubClasses.chatInput}
                            style={{
                                ...styles.chatInput,
                                ...(compact ? { height: "32rem", fontSize: "13rem", padding: "6rem 9rem", backgroundColor: "rgba(20, 27, 34, 0.55)" } : {}),
                                ...(showRemaining ? { paddingRight: "84rem" } : {}),
                            }}
                            value={draft}
                            placeholder={t("CS2MP.UI.MessagePlaceholder", "Message the other players...")}
                            aria-label={t("CS2MP.UI.Chat", "Chat")}
                            aria-describedby={showRemaining ? "cs2mp-chat-remaining" : undefined}
                            maxLength={MAX_CHAT_LENGTH}
                            spellCheck={false}
                            autoComplete="off"
                            onFocus={() => setTyping(true)}
                            onBlur={() => setTyping(false)}
                            onMouseDown={(e) => e.stopPropagation()}
                            onKeyDown={(e) => {
                                e.stopPropagation();
                                if (e.key === "Enter" && !e.nativeEvent.isComposing) {
                                    send();
                                } else if (e.key === "Escape") {
                                    e.preventDefault();
                                    e.currentTarget.blur();
                                }
                            }}
                            onChange={(e) => setDraft((e.target as HTMLInputElement).value)}
                        />
                    </InputActionBarrier>
                    {showRemaining ? <span id="cs2mp-chat-remaining" style={{
                        ...styles.composerRemaining,
                        ...(remaining <= 20 ? { color: "#ffc6c6" } : {}),
                    }}>{t("CS2MP.UI.CharactersLeft", "{0} left").replace("{0}", String(remaining))}</span> : null}
                </div>
                <HubButton tone="primary" style={{
                    ...styles.sendButton,
                    ...(compact ? { height: "32rem", minHeight: "32rem", minWidth: "52rem", fontSize: "12rem", padding: "5rem 8rem" } : {}),
                }} disabled={!draft.trim()} onSelect={send}>
                    {t(LOC.send, "Send")}
                </HubButton>
            </div>
            {!compact ? <div style={styles.sessionFooter}>
                <div style={styles.sessionFooterButton}>
                    <HubButton
                        style={styles.sessionActionButton}
                        disabled={!canPingMap}
                        hint={t("CS2MP.UI.PingMapHint", "Show everyone the map location at the center of your screen.")}
                        onSelect={() => trigger(GROUP, "pingMap")}>
                        {t("CS2MP.UI.PingMap", "Ping map location")}
                    </HubButton>
                </div>
                <div style={styles.sessionSecondaryButton}>
                    {isHost ? <HubButton
                        tone="danger"
                        style={styles.sessionActionButton}
                        hint={t("CS2MP.UI.EndSessionHint", "End hosting and disconnect every player.")}
                        onSelect={() => trigger(GROUP, "requestDisconnect")}>
                        {t(LOC.closeSession, "Close Session")}
                    </HubButton> : <HubButton
                        style={styles.sessionActionButton}
                        disabled={showActivity}
                        hint={t("CS2MP.UI.RequestWorldHint", "Ask the host to send its city again.")}
                        onSelect={() => trigger(GROUP, "syncNow")}>
                        {t("CS2MP.UI.SendWorld", "Send World")}
                    </HubButton>}
                </div>
            </div> : null}
        </>
    );

    const sessionView = (
        <div style={styles.scrollArea}>
            <HubButton
                style={styles.sessionActionButton}
                disabled={showActivity}
                hint={isHost
                    ? t("CS2MP.UI.ReloadCityHint", "Reload the host's city for all players. The shared game pauses while it transfers.")
                    : t("CS2MP.UI.RequestWorldHint", "Ask the host to send its city again.")}
                onSelect={() => trigger(GROUP, "syncNow")}>
                {t("CS2MP.UI.ResendCity", "Resend city")}
            </HubButton>
            <OutlinedSection title={t(LOC.sessionActivity, "Session activity")} style={{ marginTop: "22rem" }}>
                <div style={styles.sectionContent}>
                    {activity.length === 0 ? <div style={styles.toolHint}>{t("CS2MP.UI.NoActivity", "Session events will appear here.")}</div> :
                        [...activity].reverse().map(entry => <div key={entry.id} style={{
                            ...styles.systemLine,
                            ...(entry.kind === "error" ? { color: "#ffc6c6" } : {}),
                        }}>
                            <div style={styles.activityTime}>{entry.time}</div>
                            <div style={styles.activityText}>{entry.text}</div>
                        </div>)}
                </div>
            </OutlinedSection>
        </div>
    );

    return (
        <PanelBody
            top={!compact && (view === "chat" || showActivity) ? topBlock : undefined}
            middle={!compact && view === "session" ? sessionView : chatFeed}
            bottom={compact || view === "chat" ? bottomBlock : undefined}
        />
    );
};

// ---- Movable/resizable panel ------------------------------------------------------

// Default geometry is rem-anchored next to the right menu; the first drag or
// resize snapshots the rendered px rect and the panel is free after that.
// Kept by the parent so the panel reopens where the user left it.
export interface PanelGeometry {
    pos: { x: number; y: number } | null;
    size: { w: number; h: number } | null;
}

const MIN_W = 360;
const MIN_H = 420;
const COMPACT_W = 340;
const COMPACT_H = 280;
const PANEL_SWITCH_MS = 180;
const PANEL_SWITCH_TRANSITION = `left ${PANEL_SWITCH_MS}ms ease-out, top ${PANEL_SWITCH_MS}ms ease-out, width ${PANEL_SWITCH_MS}ms ease-out, height ${PANEL_SWITCH_MS}ms ease-out, background-color ${PANEL_SWITCH_MS}ms ease-out`;

interface DragState {
    mode: "move" | "resize";
    startX: number;
    startY: number;
    baseX: number;
    baseY: number;
    baseW: number;
    baseH: number;
}

export const MultiplayerPanel = ({ entries, players, view, onView, geometry, onGeometry, minimized, expandedSize, onToggleMinimized, onClose }: {
    entries: ChatEntry[];
    players: PlayerEntry[];
    view: HubView;
    onView: (view: HubView) => void;
    geometry: PanelGeometry;
    onGeometry: (geometry: PanelGeometry) => void;
    minimized: boolean;
    expandedSize: PanelGeometry["size"];
    onToggleMinimized: (current: PanelGeometry, next: PanelGeometry) => void;
    onClose: () => void;
}) => {
    const t = useT();
    const inSession = useValue(inSession$);
    const panelRef = useRef<HTMLDivElement | null>(null);
    const headerRef = useRef<HTMLDivElement | null>(null);
    const dragRef = useRef<DragState | null>(null);
    const draggedGeometryRef = useRef<PanelGeometry | null>(null);
    const [switchingSize, setSwitchingSize] = useState(false);
    const switchTimerRef = useRef<number | undefined>();
    const switchFrameRef = useRef<number | undefined>();

    useEffect(() => () => {
        window.clearTimeout(switchTimerRef.current);
        if (switchFrameRef.current !== undefined) cancelAnimationFrame(switchFrameRef.current);
    }, []);

    const toggleMinimized = () => {
        const panel = panelRef.current;
        if (!panel || switchingSize) return;
        const rect = panel.getBoundingClientRect();
        const scale = (headerRef.current?.getBoundingClientRect().height || 42) / 42;
        const requested = minimized
            ? expandedSize || { w: 460 * scale, h: 660 * scale }
            : { w: COMPACT_W * scale, h: COMPACT_H * scale };
        const size = {
            w: Math.min(requested.w, window.innerWidth - 32 * scale),
            h: Math.min(requested.h, window.innerHeight - 100 * scale),
        };
        // Keep the bottom-right corner in place, and fit a restored window on screen.
        const pos = {
            x: Math.max(8 * scale, Math.min(rect.right - size.w, window.innerWidth - size.w - 8 * scale)),
            y: Math.max(8 * scale, Math.min(rect.bottom - size.h, window.innerHeight - size.h - 8 * scale)),
        };
        const current = { pos: { x: rect.left, y: rect.top }, size: { w: rect.width, h: rect.height } };

        // A rem-anchored window has auto top/left. Normalize its current pixel box
        // before changing dimensions so the browser can interpolate both modes.
        panel.style.transition = "none";
        panel.style.left = rect.left + "px";
        panel.style.top = rect.top + "px";
        panel.style.right = "auto";
        panel.style.bottom = "auto";
        panel.style.width = rect.width + "px";
        panel.style.height = rect.height + "px";
        window.clearTimeout(switchTimerRef.current);
        setSwitchingSize(true);
        // Cohtml lays out asynchronously. Give the normalized box one full frame
        // before applying its target, otherwise auto-to-pixel positions jump.
        switchFrameRef.current = requestAnimationFrame(() => {
            switchFrameRef.current = requestAnimationFrame(() => {
                switchFrameRef.current = undefined;
                panel.style.transition = PANEL_SWITCH_TRANSITION;
                onToggleMinimized(current, { pos, size });
                switchTimerRef.current = window.setTimeout(() => setSwitchingSize(false), PANEL_SWITCH_MS + 30);
            });
        });
    };

    useEffect(() => {
        const onMove = (e: MouseEvent) => {
            const drag = dragRef.current;
            if (!drag) return;
            const dx = e.clientX - drag.startX;
            const dy = e.clientY - drag.startY;
            if (drag.mode === "move") {
                const x = Math.min(Math.max(drag.baseX + dx, 60 - drag.baseW), window.innerWidth - 60);
                const y = Math.min(Math.max(drag.baseY + dy, 0), window.innerHeight - 60);
                draggedGeometryRef.current = { pos: { x, y }, size: { w: drag.baseW, h: drag.baseH } };
                // Move the rendered panel directly. Updating parent React state on every
                // pointer event re-rendered and re-measured the whole chat/player tree.
                const panel = panelRef.current;
                if (panel) panel.style.transform = `translate(${x - drag.baseX}px, ${y - drag.baseY}px)`;
            } else {
                const w = Math.min(Math.max(drag.baseW + dx, MIN_W), window.innerWidth);
                const h = Math.min(Math.max(drag.baseH + dy, MIN_H), window.innerHeight);
                const next = { pos: { x: drag.baseX, y: drag.baseY }, size: { w, h } };
                draggedGeometryRef.current = next;
                onGeometry(next);
            }
        };
        const onUp = () => {
            if (!dragRef.current) return;
            dragRef.current = null;
            const next = draggedGeometryRef.current;
            draggedGeometryRef.current = null;
            const panel = panelRef.current;
            if (next?.pos && panel) {
                panel.style.left = next.pos.x + "px";
                panel.style.top = next.pos.y + "px";
                panel.style.right = "auto";
                panel.style.bottom = "auto";
                panel.style.transform = "none";
            }
            if (next) onGeometry(next);
        };
        document.addEventListener("mousemove", onMove);
        document.addEventListener("mouseup", onUp);
        window.addEventListener("blur", onUp);
        return () => {
            document.removeEventListener("mousemove", onMove);
            document.removeEventListener("mouseup", onUp);
            window.removeEventListener("blur", onUp);
        };
    }, [onGeometry]);

    const beginDrag = (e: ReactMouseEvent, mode: "move" | "resize") => {
        const el = panelRef.current;
        if (!el || e.button !== 0) return;
        window.clearTimeout(switchTimerRef.current);
        if (switchFrameRef.current !== undefined) cancelAnimationFrame(switchFrameRef.current);
        switchFrameRef.current = undefined;
        el.style.transition = "none";
        setSwitchingSize(false);
        const rect = el.getBoundingClientRect();
        draggedGeometryRef.current = null;
        dragRef.current = {
            mode,
            startX: e.clientX,
            startY: e.clientY,
            baseX: rect.left,
            baseY: rect.top,
            baseW: rect.width,
            baseH: rect.height,
        };
        e.preventDefault();
        e.stopPropagation();
    };

    const panelStyle: CSSProperties = {
        ...styles.panel,
        ...(minimized ? { width: `${COMPACT_W}rem`, height: `${COMPACT_H}rem`, backgroundColor: "rgba(34, 49, 65, 0.8)" } : {}),
        transition: switchingSize ? PANEL_SWITCH_TRANSITION : "none",
    };
    if (geometry.pos) {
        panelStyle.left = geometry.pos.x + "px";
        panelStyle.top = geometry.pos.y + "px";
        panelStyle.right = "auto";
        panelStyle.bottom = "auto";
    }
    if (geometry.size) {
        panelStyle.width = geometry.size.w + "px";
        panelStyle.height = geometry.size.h + "px";
    }

    const title = minimized
        ? t("CS2MP.UI.Chat", "Chat")
        : inSession && view === "session"
            ? t(LOC.sessionControls, "Session controls")
            : t(LOC.multiplayer, "Multiplayer");

    return (
        <Portal>
            <div ref={panelRef} style={panelStyle} onMouseDown={(e) => e.stopPropagation()}>
                <div ref={headerRef} style={{
                    ...styles.header,
                    ...(minimized ? { backgroundColor: "rgba(20, 27, 34, 0.78)" } : {}),
                    transition: "background-color 180ms ease-out",
                }} onMouseDown={(e) => beginDrag(e, "move")}>
                    <div style={{ ...styles.headerLeading, width: inSession ? minimized ? "68rem" : "102rem" : "34rem" }}>
                        {inSession && !minimized && view !== "chat" ? <HeaderIconButton
                            src={ICON_BACK}
                            tooltip={t(LOC.back, "Back")}
                            onSelect={() => onView("chat")}
                        /> : null}
                    </div>
                    <div style={styles.headerTitle}>{title}</div>
                    <div style={styles.headerActions}>
                        {inSession && !minimized ? <HeaderIconButton
                            src={ICON_GEAR}
                            tooltip={t(LOC.sessionControls, "Session controls")}
                            selected={view === "session"}
                            onSelect={() => onView(view === "session" ? "chat" : "session")}
                        /> : null}
                        {inSession ? <HeaderIconButton
                            tooltip={minimized
                                ? t("CS2MP.UI.ExpandChat", "Expand chat")
                                : t("CS2MP.UI.MinimizeChat", "Minimize chat")}
                            disabled={switchingSize}
                            onSelect={toggleMinimized}
                        ><PanelSizeIcon minimized={minimized} /></HeaderIconButton> : null}
                        <HeaderIconButton
                            src={ICON_CLOSE}
                            tooltip={t(LOC.close, "Close")}
                            onSelect={onClose}
                        />
                    </div>
                </div>
                {inSession
                    ? <SessionView entries={entries} players={players} view={view} compact={minimized} />
                    : <HostSetupView />}
                {!minimized ? <div style={styles.resizeHandle} onMouseDown={(e) => beginDrag(e, "resize")}>
                    <div style={styles.resizeGrip} />
                </div> : null}
            </div>
        </Portal>
    );
};

// ---- Right-menu button (appended above notifications/Chirper) -------------------

// Host-only prompts shown whenever players are waiting to join or resync.
// It floats at the top of the screen (not a full-screen blocker) so the host can
// keep playing and answer each request when ready. Always mounted with the right-menu
// button, so it appears even when the hub panel is closed.
const ApprovalRequestModals = () => {
    const t = useT();
    const isHost = useValue(isHost$);
    const pendingJoinsJson = useValue(pendingJoins$);
    const pendingResyncsJson = useValue(pendingResyncs$);
    const pendingJoins = useMemo(() => parseArray<PendingJoin>(pendingJoinsJson), [pendingJoinsJson]);
    const pendingResyncs = useMemo(
        () => parseArray<PendingResync>(pendingResyncsJson),
        [pendingResyncsJson],
    );

    if (!isHost || (pendingJoins.length === 0 && pendingResyncs.length === 0)) return null;

    return (
        <Portal>
            <div style={styles.joinAnchor}>
                {pendingJoins.map((join) => (
                    <InputActionBarrier key={`join-${join.id}`}>
                        <AutoNavigationScope
                            debugName="CS2MP Join Request"
                            direction={NavigationDirection.Horizontal}
                            initialFocused="accept"
                            allowLooping>
                            <div style={styles.joinCard} onMouseDown={(e) => e.stopPropagation()}>
                                <div style={styles.joinCardTitle}>{t(LOC.joinRequestTitle, "Join Request")}</div>
                                <div style={styles.joinCardBody}>
                                    {t(LOC.joinRequestBody, "{0} wants to join your session.").replace("{0}", () => join.name)}
                                </div>
                                {join.message ? <div style={styles.joinCardMessage}>{join.message}</div> : null}
                                <div style={styles.joinCardButtons}>
                                    <HubButton
                                        variant="primary"
                                        focusKey="accept"
                                        style={styles.joinCardButton}
                                        onSelect={() => trigger(GROUP, "approveJoin", join.id)}>
                                        {t(LOC.accept, "Accept")}
                                    </HubButton>
                                    <HubButton
                                        variant="flat"
                                        focusKey="decline"
                                        style={styles.joinCardButton}
                                        onSelect={() => trigger(GROUP, "declineJoin", join.id)}>
                                        {t(LOC.decline, "Decline")}
                                    </HubButton>
                                </div>
                            </div>
                        </AutoNavigationScope>
                    </InputActionBarrier>
                ))}
                {pendingResyncs.map((request) => (
                    <InputActionBarrier key={`resync-${request.id}`}>
                        <AutoNavigationScope
                            debugName="CS2MP Resync Request"
                            direction={NavigationDirection.Horizontal}
                            initialFocused="allow"
                            allowLooping>
                            <div style={styles.joinCard} onMouseDown={(e) => e.stopPropagation()}>
                                <div style={styles.joinCardTitle}>
                                    {t(LOC.resyncRequestTitle, "World Sync Request")}
                                </div>
                                <div style={styles.joinCardBody}>
                                    {t(LOC.resyncRequestBody, "{0} requested a world sync: {1}")
                                        .replace("{0}", request.name)
                                        .replace("{1}", request.reason)}
                                </div>
                                <div style={styles.joinCardButtons}>
                                    <HubButton
                                        variant="primary"
                                        focusKey="allow"
                                        style={styles.joinCardButton}
                                        onSelect={() => trigger(GROUP, "approveResync", request.id)}>
                                        {t(LOC.accept, "Accept")}
                                    </HubButton>
                                    <HubButton
                                        variant="flat"
                                        focusKey="decline"
                                        style={styles.joinCardButton}
                                        onSelect={() => trigger(GROUP, "declineResync", request.id)}>
                                        {t(LOC.decline, "Decline")}
                                    </HubButton>
                                </div>
                            </div>
                        </AutoNavigationScope>
                    </InputActionBarrier>
                ))}
            </div>
        </Portal>
    );
};

export const MultiplayerRightMenuButton = () => {
    const t = useT();
    const [open, setOpen] = useState(false);
    const [geometry, setGeometry] = useState<PanelGeometry>({ pos: null, size: null });
    const [compactGeometry, setCompactGeometry] = useState<PanelGeometry>({ pos: null, size: null });
    const [minimized, setMinimized] = useState(false);
    const [view, setView] = useState<HubView>("chat");
    const chatJson = useValue(chatLog$);
    const playerJson = useValue(playerList$);
    const inSession = useValue(inSession$);
    const accepted = useValue(disclaimerAccepted$);
    const entries = useMemo(() => parseArray<ChatEntry>(chatJson), [chatJson]);
    const players = useMemo(() => parseArray<PlayerEntry>(playerJson), [playerJson]);

    const latestId = entries.length > 0 ? entries[entries.length - 1].id : 0;
    const [readSeenId, setReadSeenId] = useState(latestId);

    useEffect(() => {
        if ((open && accepted && view === "chat") || !inSession) {
            setReadSeenId(latestId);
        }
        if (!inSession) {
            setView("chat");
            setMinimized(false);
        }
    }, [open, accepted, view, inSession, latestId]);

    // Only another player's messages need attention. Session events stay inside the hub.
    const unread = !inSession ? 0 : entries.filter(entry =>
        entry.kind === "message" && !entry.isLocal && entry.id > readSeenId).length;
    const title = t(LOC.multiplayer, "Multiplayer");

    return (
        <>
            <ApprovalRequestModals />
            <Tooltip tooltip={title} direction="left">
                <div style={styles.buttonWrap} className={rmMenu ? rmMenu.item : undefined}>
                    <Button
                        theme={rmButton ? { button: rmButton.button, icon: rmButton.icon } : undefined}
                        className={rmButton ? rmButton.toggleStates : undefined}
                        style={rmButton ? undefined : styles.fallbackButton}
                        selected={open}
                        onSelect={() => setOpen(!open)}>
                        <img
                            src={ICON_MULTIPLAYER}
                            className={rmButton ? rmButton.icon : undefined}
                            style={rmButton ? undefined : styles.fallbackIcon}
                        />
                    </Button>
                    {unread > 0 ? <div style={styles.unreadBadge}>{unread > 9 ? "9+" : unread}</div> : null}
                </div>
            </Tooltip>
            {open ? (
                accepted ? (
                    <MultiplayerPanel
                        entries={entries}
                        players={players}
                        view={view}
                        onView={setView}
                        geometry={inSession && minimized ? compactGeometry : geometry}
                        onGeometry={minimized ? setCompactGeometry : setGeometry}
                        minimized={inSession && minimized}
                        expandedSize={geometry.size}
                        onToggleMinimized={(current, next) => {
                            if (minimized) {
                                setCompactGeometry(current);
                                setGeometry(next);
                            } else {
                                setGeometry(current);
                                setCompactGeometry(next);
                            }
                            setView("chat");
                            setMinimized(!minimized);
                        }}
                        onClose={() => setOpen(false)}
                    />
                ) : (
                    // First use: the disclaimer stands in for the panel until accepted.
                    // Accepting flips the binding, which swaps in the panel on re-render.
                    <DisclaimerModal onAccept={() => {}} onDecline={() => setOpen(false)} />
                )
            ) : null}
        </>
    );
};
