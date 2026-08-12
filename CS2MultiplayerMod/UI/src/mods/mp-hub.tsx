import { bindValue, trigger, useValue } from "cs2/api";
import { AutoNavigationScope, InputActionBarrier, NavigationDirection } from "cs2/input";
import { useLocalization } from "cs2/l10n";
import { getModule } from "cs2/modding";
import { Button, Portal, Tooltip } from "cs2/ui";
import {
    CONNECTION_DIRECT,
    CONNECTION_LOC,
    CONNECTION_RELAY,
    ConnectionSegmented,
    JoinCodeDisplay,
} from "mods/connection-picker";
import { CSSProperties, MouseEvent as ReactMouseEvent, useEffect, useMemo, useRef, useState } from "react";
import { DisclaimerModal, disclaimerAccepted$ } from "mods/disclaimer";
import { TransferProgress } from "mods/transfer-progress";
import { VersionWarningBanner } from "mods/version-banner";

// Binding group shared with MultiplayerUISystem (same group as the join dialog).
const GROUP = "cs2mp";

// Locale keys served by the mod's LocaleEN/LocaleDE sources (L10n.Key constants).
const LOC = {
    multiplayer: "CS2MP.UI.Multiplayer",
    sessionSettings: "CS2MP.UI.SessionSettings",
    back: "CS2MP.UI.Back",
    chatPlaceholder: "CS2MP.UI.ChatPlaceholder",
    send: "CS2MP.UI.Send",
    noMessages: "CS2MP.UI.NoMessages",
    hostSession: "CS2MP.UI.HostSession",
    lanOnly: "CS2MP.UI.LanOnly",
    ...CONNECTION_LOC,
    maxPlayers: "CS2MP.UI.MaxPlayers",
    resyncMinutes: "CS2MP.UI.ResyncMinutes",
    syncWorld: "CS2MP.UI.SyncWorld",
    saveCopy: "CS2MP.UI.SaveCopy",
    saveCopyTitle: "CS2MP.UI.SaveCopyTitle",
    saveCopyBody: "CS2MP.UI.SaveCopyBody",
    worldName: "CS2MP.UI.WorldName",
    saveToPC: "CS2MP.UI.SaveToPC",
    savingCopy: "CS2MP.UI.SavingCopy",
    saveCopySuccess: "CS2MP.UI.SaveCopySuccess",
    saveCopyExists: "CS2MP.UI.SaveCopyExists",
    saveCopyInvalid: "CS2MP.UI.SaveCopyInvalid",
    saveCopyUnavailable: "CS2MP.UI.SaveCopyUnavailable",
    saveCopyFailed: "CS2MP.UI.SaveCopyFailed",
    multiplayerWorld: "CS2MP.UI.MultiplayerWorld",
    suggestedCopyName: "CS2MP.UI.SuggestedCopyName",
    cancel: "CS2MP.UI.Cancel",
    close: "CS2MP.UI.Close",
    sendingWorld: "CS2MP.UI.SendingWorld",
    lockedInSession: "CS2MP.UI.LockedInSession",
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
    joinRequestTitle: "CS2MP.UI.JoinRequestTitle",
    joinRequestBody: "CS2MP.UI.JoinRequestBody",
    accept: "CS2MP.UI.Accept",
    decline: "CS2MP.UI.Decline",
    playerName: "CS2MP.UI.PlayerName",
    port: "CS2MP.UI.Port",
    password: "CS2MP.UI.Password",
    disconnect: "CS2MP.UI.Disconnect",
};

const useT = () => {
    const { translate } = useLocalization();
    return (id: string, fallback: string) => translate(id, fallback) ?? fallback;
};

// All vanilla glyphs verified to exist in Cities2_Data\Content\Game\UI\Media\Glyphs.
const ICON_MULTIPLAYER = "Media/Glyphs/Passenger.svg";
const ICON_GEAR = "Media/Glyphs/Gear.svg";
const ICON_CLOSE = "Media/Glyphs/Close.svg";
const ICON_CHECK = "Media/Glyphs/Checkmark.svg";

// ---- Bindings (in addition to the ones the join dialog already uses) ---------

const chatLog$ = bindValue<string>(GROUP, "chatLog", "[]");
const inSession$ = bindValue<boolean>(GROUP, "inSession", false);
const isHost$ = bindValue<boolean>(GROUP, "isHost", false);
const canHost$ = bindValue<boolean>(GROUP, "canHost", false);
const playerCount$ = bindValue<number>(GROUP, "playerCount", 0);
const statusKind$ = bindValue<string>(GROUP, "statusKind", "offline");
const statusTitle$ = bindValue<string>(GROUP, "statusTitle", "Offline");
const statusDetail$ = bindValue<string>(GROUP, "statusDetail", "");
const statusHelp$ = bindValue<string>(GROUP, "statusHelp", "");
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
const resyncMinutes$ = bindValue<string>(GROUP, "resyncMinutes", "15");
const playerList$ = bindValue<string>(GROUP, "playerList", "[]");
const pendingJoins$ = bindValue<string>(GROUP, "pendingJoins", "[]");
const canSaveClientWorld$ = bindValue<boolean>(GROUP, "canSaveClientWorld", false);
const clientWorldSaveStatus$ = bindValue<string>(GROUP, "clientWorldSaveStatus", "idle");
const clientWorldSaveName$ = bindValue<string>(GROUP, "clientWorldSaveName", "");
const cityName$ = bindValue<string>("toolbarBottom", "cityName", "");

interface ChatEntry {
    id: number;
    sender: string | null; // null = system/event line ("X joined.")
    text: string;
    time: string;
}

interface PlayerEntry {
    id: number;
    name: string;
    isHost: boolean;
}

interface PendingJoin {
    id: number;
    name: string;
}

const parseChatLog = (json: string): ChatEntry[] => {
    try {
        const parsed = JSON.parse(json);
        return Array.isArray(parsed) ? parsed : [];
    } catch {
        return [];
    }
};

const parsePlayerList = (json: string): PlayerEntry[] => {
    try {
        const parsed = JSON.parse(json);
        return Array.isArray(parsed) ? parsed : [];
    } catch {
        return [];
    }
};

const parsePendingJoins = (json: string): PendingJoin[] => {
    try {
        const parsed = JSON.parse(json);
        return Array.isArray(parsed) ? parsed : [];
    } catch {
        return [];
    }
};

// Vanilla right-menu styling so the button is indistinguishable from the
// Chirper/notification buttons below it. The module paths are vanilla-internal
// and may move on a game update, hence the inline fallback look.
const tryModule = (path: string, exportName: string): any => {
    try {
        return getModule(path, exportName);
    } catch {
        return null;
    }
};
const tryClasses = (path: string): Record<string, string> | null =>
    tryModule(path, "classes");
const rmButton = tryClasses("game-ui/game/components/right-menu/right-menu-button.module.scss");
const rmMenu = tryClasses("game-ui/game/components/right-menu/right-menu.module.scss");

// Status-kind accents shared with the join dialog's indicator (used for the dot).
const kindColors: Record<string, string> = {
    offline: "#8fa0b3",
    disabled: "#8fa0b3",
    connecting: "#72c8f0",
    syncing: "#72c8f0",
    connected: "#8ee08c",
    error: "#ff8a7a",
};

// rem behaves like resolution-independent pixels (the game scales root font size).
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
    statusDot: {
        position: "absolute",
        right: "1rem",
        top: "1rem",
        width: "9rem",
        height: "9rem",
        borderRadius: "50%",
        border: "1rem solid rgba(0, 0, 0, 0.5)",
        pointerEvents: "none",
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
    toastAnchor: {
        position: "absolute",
        right: "100%",
        marginRight: "12rem",
        top: "50%",
        transform: "translateY(-50%)",
        width: "320rem",
        display: "flex",
        flexDirection: "column",
        alignItems: "flex-end",
        pointerEvents: "none",
    },
    toast: {
        maxWidth: "320rem",
        backgroundColor: "rgba(24, 33, 51, 0.95)",
        borderLeft: "3rem solid #72c8f0",
        borderRadius: "3rem",
        padding: "6rem 10rem",
        marginTop: "4rem",
        boxShadow: "0 4rem 12rem rgba(0, 0, 0, 0.4)",
        fontSize: "13rem",
        color: "#ffffff",
    },
    toastSender: {
        color: "#9dc1de",
        textTransform: "uppercase",
        fontSize: "11rem",
        marginRight: "6rem",
    },
    toastSystem: {
        color: "rgba(255, 255, 255, 0.75)",
        fontStyle: "italic",
    },
    panel: {
        position: "fixed",
        right: "64rem",
        top: "50%",
        transform: "translateY(-50%)",
        width: "440rem",
        // Definite height: the flex chain below (body → chat list) can only
        // distribute space the panel actually has, so "auto" would re-introduce
        // the buttons-in-the-middle look.
        height: "520rem",
        display: "flex",
        flexDirection: "column",
        backgroundColor: "rgba(24, 33, 51, 0.97)",
        borderRadius: "4rem",
        boxShadow: "0 16rem 48rem rgba(0, 0, 0, 0.45)",
        zIndex: 900,
        pointerEvents: "auto",
        // Content must never paint outside the panel background — when the user
        // resizes below the natural content height, the inner areas scroll instead.
        overflow: "hidden",
    },
    header: {
        display: "flex",
        alignItems: "center",
        padding: "12rem 14rem",
        borderBottom: "1rem solid rgba(157, 193, 222, 0.2)",
        flexShrink: 0,
    },
    headerTitle: {
        flex: 1,
        fontSize: "16rem",
        color: "#ffffff",
        textTransform: "uppercase",
    },
    headerButton: {
        width: "32rem",
        height: "32rem",
        marginLeft: "6rem",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        borderRadius: "50%",
        transition: "background-color 120ms ease, opacity 120ms ease",
    },
    // flexGrow/flexBasis spelled out instead of the "flex" shorthand: the game's
    // Gameface runtime does not reliably expand column children from the shorthand.
    body: {
        display: "flex",
        flexDirection: "column",
        padding: "12rem 14rem",
        flexGrow: 1,
        flexShrink: 1,
        flexBasis: "0%",
        minHeight: 0,
        overflow: "hidden",
    },
    // Fields live in here so a small panel scrolls them while the footer
    // (action buttons) stays pinned to the panel bottom.
    scrollArea: {
        flexGrow: 1,
        flexShrink: 1,
        flexBasis: "0%",
        minHeight: 0,
        overflowY: "auto",
    },
    playerCountRow: {
        marginBottom: "6rem",
        flexShrink: 0,
        fontSize: "13rem",
        color: "#9dc1de",
        textTransform: "uppercase",
    },
    chatList: {
        flexGrow: 1,
        flexShrink: 1,
        flexBasis: "0%",
        minHeight: "80rem",
        overflowY: "auto",
        backgroundColor: "rgba(0, 0, 0, 0.3)",
        border: "1rem solid rgba(157, 193, 222, 0.2)",
        borderRadius: "3rem",
        padding: "8rem 10rem",
        marginBottom: "10rem",
    },
    chatEmpty: {
        fontSize: "13rem",
        color: "rgba(255, 255, 255, 0.45)",
        fontStyle: "italic",
        textAlign: "center",
        marginTop: "12rem",
    },
    chatLine: {
        fontSize: "14rem",
        color: "#ffffff",
        marginBottom: "4rem",
        wordBreak: "break-word",
    },
    chatTime: {
        color: "rgba(255, 255, 255, 0.4)",
        fontSize: "11rem",
        marginRight: "6rem",
    },
    chatSender: {
        color: "#9dc1de",
    },
    systemLine: {
        fontSize: "12.5rem",
        color: "#72c8f0",
        fontStyle: "italic",
        margin: "3rem 0 5rem 0",
        textAlign: "center",
        wordBreak: "break-word",
    },
    inputRow: {
        display: "flex",
        alignItems: "center",
        marginBottom: "10rem",
        flexShrink: 0,
    },
    chatInput: {
        flex: 1,
        fontSize: "14rem",
        color: "#ffffff",
        backgroundColor: "rgba(0, 0, 0, 0.35)",
        border: "1rem solid rgba(157, 193, 222, 0.35)",
        borderRadius: "3rem",
        padding: "6rem 10rem",
    },
    sendButton: {
        marginLeft: "8rem",
        padding: "6rem 14rem",
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
    hint: {
        fontSize: "12.5rem",
        color: "rgba(255, 255, 255, 0.55)",
        margin: "2rem 0 12rem 0",
    },
    errorLine: {
        fontSize: "13rem",
        color: "#ffd7d1",
        backgroundColor: "rgba(205, 82, 70, 0.18)",
        borderLeft: "3rem solid #ff8a7a",
        borderRadius: "3rem",
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
    lockedNote: {
        fontSize: "12rem",
        color: "rgba(255, 200, 130, 0.8)",
        marginBottom: "10rem",
    },
    row: {
        display: "flex",
        alignItems: "center",
        marginBottom: "10rem",
    },
    label: {
        width: "150rem",
        fontSize: "13.5rem",
        color: "#9dc1de",
        textTransform: "uppercase",
        flexShrink: 0,
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
    playerSection: {
        flexShrink: 0,
        marginBottom: "10rem",
    },
    sectionHeader: {
        display: "flex",
        alignItems: "center",
        justifyContent: "space-between",
        marginBottom: "5rem",
        color: "#9dc1de",
        fontSize: "12.5rem",
        textTransform: "uppercase",
    },
    playerList: {
        backgroundColor: "rgba(0, 0, 0, 0.3)",
        border: "1rem solid rgba(157, 193, 222, 0.2)",
        borderRadius: "3rem",
        maxHeight: "145rem",
        overflowY: "auto",
    },
    playerRow: {
        minHeight: "36rem",
        padding: "4rem 7rem",
        display: "flex",
        alignItems: "center",
        borderBottom: "1rem solid rgba(157, 193, 222, 0.14)",
    },
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
        color: "rgba(255, 255, 255, 0.62)",
        fontSize: "10.5rem",
        textTransform: "uppercase",
    },
    kickButton: {
        marginLeft: "7rem",
        padding: "3rem 8rem",
        minWidth: "52rem",
        fontSize: "11.5rem",
    },
    banButton: {
        marginLeft: "5rem",
        padding: "3rem 8rem",
        minWidth: "52rem",
        fontSize: "11.5rem",
        color: "#ff9a8e",
    },
    confirmKickButton: {
        marginLeft: "5rem",
        padding: "3rem 7rem",
        fontSize: "11.5rem",
        color: "#ff9a8e",
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
        borderRadius: "4rem",
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
    joinCardButtons: {
        display: "flex",
        justifyContent: "flex-end",
    },
    joinCardButton: {
        marginLeft: "10rem",
        padding: "7rem 18rem",
    },
    saveDialogOverlay: {
        position: "fixed",
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        zIndex: 10003,
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        backgroundColor: "rgba(0, 0, 0, 0.62)",
        pointerEvents: "auto",
    },
    saveDialog: {
        width: "500rem",
        maxWidth: "90%",
        backgroundColor: "rgba(24, 33, 51, 0.98)",
        border: "1rem solid rgba(157, 193, 222, 0.3)",
        borderRadius: "4rem",
        padding: "22rem 24rem",
        boxShadow: "0 16rem 48rem rgba(0, 0, 0, 0.5)",
        pointerEvents: "auto",
    },
    saveDialogTitle: {
        color: "#ffffff",
        fontSize: "23rem",
        textTransform: "uppercase",
        marginBottom: "10rem",
    },
    saveDialogBody: {
        color: "rgba(255, 255, 255, 0.78)",
        fontSize: "14rem",
        lineHeight: "1.4",
        marginBottom: "18rem",
    },
    saveDialogLabel: {
        color: "#9dc1de",
        fontSize: "12rem",
        textTransform: "uppercase",
        marginBottom: "6rem",
    },
    saveDialogInput: {
        width: "100%",
        boxSizing: "border-box",
        color: "#ffffff",
        backgroundColor: "rgba(0, 0, 0, 0.36)",
        border: "1rem solid rgba(157, 193, 222, 0.4)",
        borderRadius: "3rem",
        fontSize: "16rem",
        padding: "9rem 11rem",
        marginBottom: "12rem",
    },
    saveDialogStatus: {
        minHeight: "20rem",
        color: "#ffd7d1",
        fontSize: "13rem",
        lineHeight: "1.35",
        marginBottom: "12rem",
    },
    saveDialogSuccess: {
        color: "#8ee08c",
    },
    saveDialogButtons: {
        display: "flex",
        justifyContent: "flex-end",
    },
    saveDialogButton: {
        marginLeft: "10rem",
        padding: "8rem 18rem",
    },
};

// ---- Form building blocks -----------------------------------------------------

interface HubFieldProps {
    label: string;
    value: string;
    secret?: boolean;
    disabled?: boolean;
    onChange: (value: string) => void;
}

// Text field with an InputActionBarrier while focused: in-game nearly every
// letter is a shortcut (B = bulldozer, …), so typing must not reach the game.
const HubField = ({ label, value, secret, disabled, onChange }: HubFieldProps) => {
    const [draft, setDraft] = useState(value);
    const [editing, setEditing] = useState(false);

    useEffect(() => {
        if (!editing) setDraft(value);
    }, [value]);

    return (
        <div style={styles.row}>
            <div style={styles.label}>{label}</div>
            <InputActionBarrier disabled={!editing}>
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
                    onKeyDown={(e) => e.stopPropagation()}
                    onChange={(e) => {
                        const next = (e.target as HTMLInputElement).value;
                        setDraft(next);
                        onChange(next);
                    }}
                />
            </InputActionBarrier>
        </div>
    );
};

const HubToggle = ({ label, value, disabled, onChange }: {
    label: string;
    value: boolean;
    disabled?: boolean;
    onChange: (value: boolean) => void;
}) => (
    <div style={styles.row}>
        <div style={styles.label}>{label}</div>
        <div
            style={disabled ? { ...styles.toggleBox, ...styles.inputDisabled } : styles.toggleBox}
            onClick={() => {
                if (!disabled) onChange(!value);
            }}>
            {value ? <img src={ICON_CHECK} style={styles.toggleCheck} /> : null}
        </div>
    </div>
);

const HeaderIconButton = ({ src, tooltip, selected, onSelect }: {
    src: string;
    tooltip: string;
    selected?: boolean;
    onSelect: () => void;
}) => {
    const [hovered, setHovered] = useState(false);
    const buttonStyle = {
        ...styles.headerButton,
        backgroundColor: selected
            ? "rgba(114, 200, 240, 0.22)"
            : hovered
                ? "rgba(255, 255, 255, 0.13)"
                : "transparent",
        opacity: selected || hovered ? 1 : 0.82,
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
                onMouseDown={(e) => e.stopPropagation()}
                onMouseEnter={() => setHovered(true)}
                onMouseLeave={() => setHovered(false)}>
                <Button
                    variant="icon"
                    src={src}
                    tinted
                    selected={selected}
                    style={buttonStyle}
                    onSelect={onSelect}
                />
            </div>
        </Tooltip>
    );
};

// The host/session settings fields. Connection-defining fields are locked while
// a session runs (the running server cannot re-bind them); the re-sync interval
// is read live by the host every cycle and stays editable for the host.
const SettingsFields = () => {
    const t = useT();
    const inSession = useValue(inSession$);
    const isHost = useValue(isHost$);
    const playerName = useValue(playerName$);
    const hostPort = useValue(hostPort$);
    const hostPassword = useValue(hostPassword$);
    const maxPlayers = useValue(maxPlayers$);
    const lanOnly = useValue(lanOnly$);
    const requireApproval = useValue(requireApproval$);
    const resyncMinutes = useValue(resyncMinutes$);
    const hostConnection = useValue(hostConnection$);
    const sessionUsesRelay = useValue(sessionUsesRelay$);
    const relaySupported = useValue(relaySupported$);
    const joinCode = useValue(joinCode$);

    // In a live session follow what it actually runs on; outside one, what is
    // configured for the next.
    const relay = inSession ? sessionUsesRelay : relaySupported && hostConnection !== CONNECTION_DIRECT;

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
            <HubToggle
                label={t(LOC.requireApproval, "Approve Players")}
                value={requireApproval}
                disabled={inSession}
                onChange={(v) => trigger(GROUP, "setRequireApproval", v)}
            />
            <HubField
                label={t(LOC.resyncMinutes, "World Re-sync (min)")}
                value={resyncMinutes}
                disabled={inSession && !isHost}
                onChange={(v) => trigger(GROUP, "setResyncMinutes", v)}
            />
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

    return (
        <div style={styles.body}>
            <div style={styles.scrollArea}>
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
                    </div>
                ) : null}
            </div>
            <div style={styles.footer}>
                <Button
                    variant="primary"
                    style={styles.footerButton}
                    disabled={!canHost}
                    onSelect={() => {
                        if (canHost) trigger(GROUP, "hostStart");
                    }}>
                    {t(LOC.hostSession, "Host Session")}
                </Button>
            </div>
        </div>
    );
};

// In-session settings behind the gear icon.
const SettingsView = () => {
    const t = useT();
    return (
        <div style={styles.body}>
            <div style={styles.lockedNote}>{t(LOC.lockedInSession, "Locked while a session is running.")}</div>
            <div style={styles.scrollArea}>
                <SettingsFields />
            </div>
        </div>
    );
};

const HostPlayerList = ({ players }: { players: PlayerEntry[] }) => {
    const t = useT();
    const [pendingAction, setPendingAction] = useState<{
        playerId: number;
        action: "kick" | "ban";
    } | null>(null);

    useEffect(() => {
        if (pendingAction !== null &&
            !players.some((player) => player.id === pendingAction.playerId))
            setPendingAction(null);
    }, [players, pendingAction]);

    return (
        <div style={styles.playerSection}>
            <div style={styles.sectionHeader}>
                <span>{t(LOC.players, "Players")}</span>
                <span>{players.length}</span>
            </div>
            <div style={styles.playerList}>
                {players.map((player) => {
                    const pendingKind = pendingAction !== null &&
                        pendingAction.playerId === player.id
                        ? pendingAction.action
                        : null;
                    const confirming = pendingKind !== null;
                    return (
                        <div key={player.id} style={styles.playerRow}>
                            <div style={styles.playerName}>{player.name}</div>
                            {player.isHost ? (
                                <>
                                    <span style={styles.playerBadge}>{t(LOC.host, "Host")}</span>
                                    <span style={styles.playerBadge}>{t(LOC.you, "You")}</span>
                                </>
                            ) : confirming ? (
                                <>
                                    <Button
                                        variant="flat"
                                        style={styles.confirmKickButton}
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
                                        {pendingKind === "ban"
                                            ? t(LOC.confirmBan, "Ban?")
                                            : t(LOC.confirmKick, "Remove?")}
                                    </Button>
                                    <Button
                                        variant="flat"
                                        style={styles.confirmKickButton}
                                        onSelect={() => setPendingAction(null)}>
                                        {t(LOC.cancelKick, "Cancel")}
                                    </Button>
                                </>
                            ) : (
                                <>
                                    <Button
                                        variant="flat"
                                        style={styles.kickButton}
                                        onSelect={() => setPendingAction({
                                            playerId: player.id,
                                            action: "kick",
                                        })}>
                                        {t(LOC.kick, "Kick")}
                                    </Button>
                                    <Tooltip
                                        tooltip={t(
                                            LOC.banHint,
                                            "Remove this player and prevent them from rejoining until hosting stops.",
                                        )}
                                        direction="down">
                                        <Button
                                            variant="flat"
                                            style={styles.banButton}
                                            onSelect={() => setPendingAction({
                                                playerId: player.id,
                                                action: "ban",
                                            })}>
                                            {t(LOC.ban, "Ban")}
                                        </Button>
                                    </Tooltip>
                                </>
                            )}
                        </div>
                    );
                })}
            </div>
        </div>
    );
};

const ClientWorldSaveDialog = ({ onClose }: { onClose: () => void }) => {
    const t = useT();
    const canSave = useValue(canSaveClientWorld$);
    const saveStatus = useValue(clientWorldSaveStatus$);
    const savedName = useValue(clientWorldSaveName$);
    const cityName = useValue(cityName$).trim();
    const [draft, setDraft] = useState(() =>
        t(LOC.suggestedCopyName, "{0} - Copy")
            .replace("{0}", cityName || t(LOC.multiplayerWorld, "Multiplayer World"))
            .slice(0, 85)
            .trim());
    const [submitted, setSubmitted] = useState(false);
    const inputRef = useRef<HTMLInputElement | null>(null);

    const saving = submitted && saveStatus === "saving";
    const saved = submitted && saveStatus === "saved";

    useEffect(() => {
        inputRef.current?.focus();
        inputRef.current?.select();
    }, []);

    const submit = () => {
        const saveName = draft.trim();
        if (!canSave || saving || saved || !saveName) return;
        setSubmitted(true);
        trigger(GROUP, "saveClientWorld", saveName);
    };

    let statusText = "";
    if (submitted) {
        switch (saveStatus) {
            case "saving":
                statusText = t(LOC.savingCopy, "Saving a local copy...");
                break;
            case "saved":
                statusText = t(LOC.saveCopySuccess, "Saved to this PC as \"{0}\".")
                    .replace("{0}", savedName || draft.trim());
                break;
            case "exists":
                statusText = t(LOC.saveCopyExists,
                    "A saved world with this name already exists. Choose a different name.");
                break;
            case "invalid":
                statusText = t(LOC.saveCopyInvalid,
                    "Enter a name between 1 and 85 characters.");
                break;
            case "unavailable":
                statusText = t(LOC.saveCopyUnavailable,
                    "Wait until the host world has fully loaded before saving a copy.");
                break;
            case "failed":
                statusText = t(LOC.saveCopyFailed,
                    "The copy could not be saved. Try another name and check free disk space.");
                break;
        }
    } else if (!canSave) {
        statusText = t(LOC.saveCopyUnavailable,
            "Wait until the host world has fully loaded before saving a copy.");
    }

    return (
        <Portal>
            <InputActionBarrier>
                <AutoNavigationScope
                    debugName="CS2MP Save World Copy"
                    direction={NavigationDirection.Both}
                    initialFocused={saved ? "close" : "save-copy"}
                    allowLooping>
                <div
                    style={styles.saveDialogOverlay}
                    onMouseDown={(event) => event.stopPropagation()}>
                    <div style={styles.saveDialog}>
                        <div style={styles.saveDialogTitle}>
                            {t(LOC.saveCopyTitle, "Save a World Copy")}
                        </div>
                        <div style={styles.saveDialogBody}>
                            {t(LOC.saveCopyBody,
                                "Keep the current shared city on this PC so you can continue it later in single-player.")}
                        </div>
                        <div style={styles.saveDialogLabel}>
                            {t(LOC.worldName, "World Name")}
                        </div>
                        <input
                            ref={inputRef}
                            type="text"
                            value={draft}
                            maxLength={85}
                            disabled={saving || saved}
                            spellCheck={false}
                            autoComplete="off"
                            style={styles.saveDialogInput}
                            onMouseDown={(event) => event.stopPropagation()}
                            onChange={(event) => {
                                setDraft((event.target as HTMLInputElement).value);
                                setSubmitted(false);
                                trigger(GROUP, "resetClientWorldSaveStatus");
                            }}
                            onKeyDown={(event) => {
                                event.stopPropagation();
                                if (event.key === "Enter") submit();
                                if (event.key === "Escape" && !saving) onClose();
                            }}
                        />
                        <div style={saved
                            ? { ...styles.saveDialogStatus, ...styles.saveDialogSuccess }
                            : styles.saveDialogStatus}>
                            {statusText}
                        </div>
                        <div style={styles.saveDialogButtons}>
                            {saved ? (
                                <Button focusKey="close" variant="primary" style={styles.saveDialogButton} onSelect={onClose}>
                                    {t(LOC.close, "Close")}
                                </Button>
                            ) : (
                                <>
                                    <Button
                                        variant="primary"
                                        focusKey="save-copy"
                                        style={styles.saveDialogButton}
                                        disabled={!canSave || saving || !draft.trim()}
                                        onSelect={submit}>
                                        {saving
                                            ? t(LOC.savingCopy, "Saving a local copy...")
                                            : t(LOC.saveToPC, "Save to This PC")}
                                    </Button>
                                    <Button
                                        variant="flat"
                                        focusKey="cancel"
                                        style={styles.saveDialogButton}
                                        disabled={saving}
                                        onSelect={onClose}>
                                        {t(LOC.cancel, "Cancel")}
                                    </Button>
                                </>
                            )}
                        </div>
                    </div>
                </div>
                </AutoNavigationScope>
            </InputActionBarrier>
        </Portal>
    );
};

// Active session: player count, chat feed (player lines + "X joined." event
// lines), send box, world sync, local client copy and disconnect.
const SessionView = ({ entries, players }: { entries: ChatEntry[]; players: PlayerEntry[] }) => {
    const t = useT();
    const playerCount = useValue(playerCount$);
    const mapTransferPercent = useValue(mapTransferPercent$);
    const worldSendPercent = useValue(worldSendPercent$);
    const isHost = useValue(isHost$);
    const progressMode = useValue(progressMode$);
    const statusTitle = useValue(statusTitle$);
    const statusDetail = useValue(statusDetail$);
    const canSaveClientWorld = useValue(canSaveClientWorld$);
    const [draft, setDraft] = useState("");
    const [typing, setTyping] = useState(false);
    const [saveDialogOpen, setSaveDialogOpen] = useState(false);
    const listRef = useRef<HTMLDivElement | null>(null);

    // Keep the newest line in view (only auto-stick when already near the bottom,
    // so scrolling back through history is not yanked away by new messages).
    useEffect(() => {
        const el = listRef.current;
        if (!el) return;
        const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 60;
        if (nearBottom) el.scrollTop = el.scrollHeight;
    }, [entries.length]);

    const send = () => {
        const text = draft.trim();
        if (!text) return;
        trigger(GROUP, "sendChat", text);
        setDraft("");
    };

    const activityPercent = isHost ? worldSendPercent : mapTransferPercent;
    const showActivity = progressMode !== "none";

    return (
        <div style={styles.body}>
            {saveDialogOpen ? (
                <ClientWorldSaveDialog onClose={() => setSaveDialogOpen(false)} />
            ) : null}
            {/* Single string child: Gameface puts each adjacent bare text node on
                its own line, which split "Players: 3" into three lines. */}
            {isHost
                ? <HostPlayerList players={players} />
                : <div style={styles.playerCountRow}>{t(LOC.players, "Players") + ": " + playerCount}</div>}
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
            <div ref={listRef} style={styles.chatList}>
                {entries.length === 0 ? (
                    <div style={styles.chatEmpty}>{t(LOC.noMessages, "No messages yet.")}</div>
                ) : (
                    entries.map((entry) =>
                        entry.sender === null ? (
                            <div key={entry.id} style={styles.systemLine}>{entry.text}</div>
                        ) : (
                            <div key={entry.id} style={styles.chatLine}>
                                <span style={styles.chatTime}>{entry.time + " "}</span>
                                <span style={styles.chatSender}>{entry.sender + ": "}</span>
                                <span>{entry.text}</span>
                            </div>
                        )
                    )
                )}
            </div>
            <div style={styles.inputRow}>
                <InputActionBarrier disabled={!typing}>
                    <input
                        type="text"
                        style={styles.chatInput}
                        value={draft}
                        placeholder={t(LOC.chatPlaceholder, "Type a message - /sync requests a world sync")}
                        spellCheck={false}
                        autoComplete="off"
                        onFocus={() => setTyping(true)}
                        onBlur={() => setTyping(false)}
                        onMouseDown={(e) => e.stopPropagation()}
                        onKeyDown={(e) => {
                            e.stopPropagation();
                            if (e.key === "Enter") send();
                        }}
                        onChange={(e) => setDraft((e.target as HTMLInputElement).value)}
                    />
                </InputActionBarrier>
                <Button variant="primary" style={styles.sendButton} onSelect={send}>
                    {t(LOC.send, "Send")}
                </Button>
            </div>
            <div style={styles.footer}>
                <Button variant="flat" style={styles.footerButton} onSelect={() => trigger(GROUP, "syncNow")}>
                    {t(LOC.syncWorld, "Sync World")}
                </Button>
                {!isHost ? (
                    <Button
                        variant="flat"
                        style={styles.footerButton}
                        disabled={!canSaveClientWorld}
                        onSelect={() => {
                            trigger(GROUP, "resetClientWorldSaveStatus");
                            setSaveDialogOpen(true);
                        }}>
                        {t(LOC.saveCopy, "Save Copy")}
                    </Button>
                ) : null}
                <Button variant="flat" style={styles.footerButton} onSelect={() => trigger(GROUP, "disconnect")}>
                    {t(LOC.disconnect, "Disconnect")}
                </Button>
            </div>
        </div>
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
const MIN_H = 300;

interface DragState {
    mode: "move" | "resize";
    startX: number;
    startY: number;
    baseX: number;
    baseY: number;
    baseW: number;
    baseH: number;
}

export const MultiplayerPanel = ({ entries, players, geometry, onGeometry, onClose }: {
    entries: ChatEntry[];
    players: PlayerEntry[];
    geometry: PanelGeometry;
    onGeometry: (geometry: PanelGeometry) => void;
    onClose: () => void;
}) => {
    const t = useT();
    const inSession = useValue(inSession$);
    const [showSettings, setShowSettings] = useState(false);
    const panelRef = useRef<HTMLDivElement | null>(null);
    const dragRef = useRef<DragState | null>(null);

    // The gear view only exists during a session (outside one, the setup view
    // already shows every setting) — drop it when the session ends.
    useEffect(() => {
        if (!inSession) setShowSettings(false);
    }, [inSession]);

    useEffect(() => {
        const onMove = (e: MouseEvent) => {
            const drag = dragRef.current;
            if (!drag) return;
            const dx = e.clientX - drag.startX;
            const dy = e.clientY - drag.startY;
            if (drag.mode === "move") {
                const x = Math.min(Math.max(drag.baseX + dx, 60 - drag.baseW), window.innerWidth - 60);
                const y = Math.min(Math.max(drag.baseY + dy, 0), window.innerHeight - 60);
                onGeometry({ pos: { x, y }, size: { w: drag.baseW, h: drag.baseH } });
            } else {
                const w = Math.min(Math.max(drag.baseW + dx, MIN_W), window.innerWidth);
                const h = Math.min(Math.max(drag.baseH + dy, MIN_H), window.innerHeight);
                onGeometry({ pos: { x: drag.baseX, y: drag.baseY }, size: { w, h } });
            }
        };
        const onUp = () => {
            dragRef.current = null;
        };
        document.addEventListener("mousemove", onMove);
        document.addEventListener("mouseup", onUp);
        return () => {
            document.removeEventListener("mousemove", onMove);
            document.removeEventListener("mouseup", onUp);
        };
    }, [onGeometry]);

    const beginDrag = (e: ReactMouseEvent, mode: "move" | "resize") => {
        const el = panelRef.current;
        if (!el || e.button !== 0) return;
        const rect = el.getBoundingClientRect();
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

    const panelStyle: CSSProperties = { ...styles.panel };
    if (geometry.pos) {
        panelStyle.left = geometry.pos.x + "px";
        panelStyle.top = geometry.pos.y + "px";
        panelStyle.right = "auto";
        panelStyle.transform = "none";
    }
    if (geometry.size) {
        panelStyle.width = geometry.size.w + "px";
        panelStyle.height = geometry.size.h + "px";
        panelStyle.maxHeight = "none";
    }

    const titleText = showSettings
        ? t(LOC.sessionSettings, "Session Settings")
        : t(LOC.multiplayer, "Multiplayer");

    return (
        <Portal>
            <div ref={panelRef} style={panelStyle} onMouseDown={(e) => e.stopPropagation()}>
                <div style={styles.header} onMouseDown={(e) => beginDrag(e, "move")}>
                    <div style={styles.headerTitle}>{titleText}</div>
                    {inSession ? (
                        <HeaderIconButton
                            src={ICON_GEAR}
                            tooltip={t(LOC.sessionSettings, "Session Settings")}
                            selected={showSettings}
                            onSelect={() => setShowSettings(!showSettings)}
                        />
                    ) : null}
                    <HeaderIconButton
                        src={ICON_CLOSE}
                        tooltip={t(LOC.back, "Back")}
                        onSelect={onClose}
                    />
                </div>
                {showSettings && inSession
                    ? <SettingsView />
                    : inSession
                        ? <SessionView entries={entries} players={players} />
                        : <HostSetupView />}
                <div style={styles.resizeHandle} onMouseDown={(e) => beginDrag(e, "resize")}>
                    <div style={styles.resizeGrip} />
                </div>
            </div>
        </Portal>
    );
};

// ---- Right-menu button (appended above notifications/Chirper) -------------------

const ToastList = ({ toasts }: { toasts: ChatEntry[] }) => (
    <div style={styles.toastAnchor}>
        {toasts.map((entry) => (
            <div key={entry.id} style={styles.toast}>
                {entry.sender === null ? (
                    <span style={styles.toastSystem}>{entry.text}</span>
                ) : (
                    <>
                        <span style={styles.toastSender}>{entry.sender + " "}</span>
                        <span>{entry.text}</span>
                    </>
                )}
            </div>
        ))}
    </div>
);

// Host-only prompt shown whenever one or more players are waiting to be let in.
// It floats at the top of the screen (not a full-screen blocker) so the host can
// keep playing and admit each join when ready. Always mounted with the right-menu
// button, so it appears even when the hub panel is closed.
const JoinRequestModal = () => {
    const t = useT();
    const isHost = useValue(isHost$);
    const pendingJson = useValue(pendingJoins$);
    const pending = useMemo(() => parsePendingJoins(pendingJson), [pendingJson]);

    if (!isHost || pending.length === 0) return null;

    return (
        <Portal>
            <div style={styles.joinAnchor}>
                {pending.map((join) => (
                    <InputActionBarrier key={join.id}>
                        <AutoNavigationScope
                            debugName="CS2MP Join Request"
                            direction={NavigationDirection.Horizontal}
                            initialFocused="accept"
                            allowLooping>
                            <div style={styles.joinCard} onMouseDown={(e) => e.stopPropagation()}>
                                <div style={styles.joinCardTitle}>{t(LOC.joinRequestTitle, "Join Request")}</div>
                                <div style={styles.joinCardBody}>
                                    {t(LOC.joinRequestBody, "{0} wants to join your session.").replace("{0}", join.name)}
                                </div>
                                <div style={styles.joinCardButtons}>
                                    <Button
                                        variant="primary"
                                        focusKey="accept"
                                        style={styles.joinCardButton}
                                        onSelect={() => trigger(GROUP, "approveJoin", join.id)}>
                                        {t(LOC.accept, "Accept")}
                                    </Button>
                                    <Button
                                        variant="flat"
                                        focusKey="decline"
                                        style={styles.joinCardButton}
                                        onSelect={() => trigger(GROUP, "declineJoin", join.id)}>
                                        {t(LOC.decline, "Decline")}
                                    </Button>
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
    const chatJson = useValue(chatLog$);
    const playerJson = useValue(playerList$);
    const inSession = useValue(inSession$);
    const statusKind = useValue(statusKind$);
    const accepted = useValue(disclaimerAccepted$);
    const entries = useMemo(() => parseChatLog(chatJson), [chatJson]);
    const players = useMemo(() => parsePlayerList(playerJson), [playerJson]);

    // Read marker: everything up to this id has been seen with the panel open.
    const [readSeenId, setReadSeenId] = useState(0);
    // Toast marker: advances even while closed, so each entry toasts only once.
    const toastSeenRef = useRef(0);
    const [toasts, setToasts] = useState<ChatEntry[]>([]);
    const timersRef = useRef<number[]>([]);

    const latestId = entries.length > 0 ? entries[entries.length - 1].id : 0;

    useEffect(() => {
        if (open) {
            setReadSeenId(latestId);
            toastSeenRef.current = latestId;
            setToasts([]);
            return;
        }
        const fresh = entries.filter((e) => e.id > toastSeenRef.current);
        toastSeenRef.current = latestId;
        if (fresh.length === 0) return;
        setToasts((current) => [...current, ...fresh].slice(-3));
        const ids = fresh.map((e) => e.id);
        timersRef.current.push(window.setTimeout(() => {
            setToasts((current) => current.filter((e) => ids.indexOf(e.id) < 0));
        }, 7000));
    }, [open, latestId]);

    // First mount: do not toast the entire backlog of an older session.
    useEffect(() => {
        toastSeenRef.current = latestId;
        setReadSeenId(latestId);
        return () => timersRef.current.forEach((id) => window.clearTimeout(id));
    }, []);

    const unread = open ? 0 : entries.filter((e) => e.id > readSeenId).length;
    const dotColor = kindColors[statusKind] || kindColors.offline;
    const title = t(LOC.multiplayer, "Multiplayer");

    return (
        <>
            <JoinRequestModal />
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
                    <div style={{ ...styles.statusDot, backgroundColor: dotColor }} />
                    {unread > 0 ? <div style={styles.unreadBadge}>{unread > 9 ? "9+" : unread}</div> : null}
                    {!open && inSession && toasts.length > 0 ? <ToastList toasts={toasts} /> : null}
                </div>
            </Tooltip>
            {open ? (
                accepted ? (
                    <MultiplayerPanel
                        entries={entries}
                        players={players}
                        geometry={geometry}
                        onGeometry={setGeometry}
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
