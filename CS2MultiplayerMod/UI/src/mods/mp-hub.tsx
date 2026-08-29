import { bindValue, trigger, useValue } from "cs2/api";
import { AutoNavigationScope, BackConsumer, InputActionBarrier, NavigationDirection } from "cs2/input";
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
import { useBackKey } from "mods/back-action";
import { DisclaimerModal, disclaimerAccepted$ } from "mods/disclaimer";
import { HELP_PAGE, OpenHelpButton } from "mods/help-link";
import { OtherModsBanner } from "mods/mods-banner";
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
    closeSession: "CS2MP.UI.CloseSession",
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
    isYou?: boolean;
    latency?: number;
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
    panel: {
        position: "fixed",
        right: "64rem",
        top: "50%",
        marginTop: "-290rem",
        width: "440rem",
        maxWidth: "92vw",
        height: "580rem",
        maxHeight: "88vh",
        backgroundColor: "rgba(16, 25, 36, 0.94)",
        borderRadius: "8rem",
        border: "1rem solid rgba(255, 255, 255, 0.08)",
        boxShadow: "0 18rem 48rem rgba(0, 0, 0, 0.65)",
        zIndex: 900,
        pointerEvents: "auto",
        overflow: "hidden",
    },
    header: {
        height: "54rem",
        boxSizing: "border-box",
        display: "flex",
        alignItems: "center",
        padding: "0 16rem",
        backgroundColor: "#101824",
        borderBottom: "1rem solid rgba(255, 255, 255, 0.08)",
        flexShrink: 0,
    },
    headerTitle: {
        flex: 1,
        fontSize: "15.5rem",
        fontWeight: "bold",
        letterSpacing: "0.6rem",
        color: "#38bdf8",
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
    body: {
        position: "absolute",
        top: "54rem",
        left: 0,
        right: 0,
        bottom: 0,
        boxSizing: "border-box",
        display: "flex",
        flexDirection: "column",
        padding: "12rem 14rem",
        overflow: "hidden",
        backgroundColor: "transparent",
    },
    bodyTop: {
        flexShrink: 0,
        marginBottom: "8rem",
    },
    bodyMiddle: {
        flex: 1,
        minHeight: 0,
        display: "flex",
        flexDirection: "column",
        overflow: "hidden",
    },
    bodyBottom: {
        flexShrink: 0,
        marginTop: "8rem",
    },
    scrollArea: {
        height: "100%",
        overflowY: "auto",
    },
    playerCountRow: {
        marginBottom: "6rem",
        flexShrink: 0,
        fontSize: "12.5rem",
        fontWeight: "bold",
        letterSpacing: "0.5rem",
        color: "#9dc1de",
        textTransform: "uppercase",
    },
    chatList: {
        height: "100%",
        boxSizing: "border-box",
        overflowY: "auto",
        backgroundColor: "rgba(10, 16, 24, 0.4)",
        border: "1rem solid rgba(157, 193, 222, 0.15)",
        borderRadius: "4rem",
        padding: "10rem 14rem",
    },
    chatEmpty: {
        fontSize: "13rem",
        color: "rgba(255, 255, 255, 0.45)",
        fontStyle: "italic",
        textAlign: "center",
        marginTop: "12rem",
    },
    chatLine: {
        fontSize: "13.5rem",
        color: "#ffffff",
        marginBottom: "4rem",
        whiteSpace: "normal",
        wordBreak: "normal",
        overflowWrap: "break-word",
        lineHeight: "1.4",
    },
    chatTime: {
        color: "rgba(255, 255, 255, 0.35)",
        fontSize: "11rem",
        marginRight: "6rem",
    },
    chatSender: {
        color: "#38bdf8",
        fontWeight: "bold",
    },
    systemLine: {
        fontSize: "12.5rem",
        color: "#cbd5e1",
        margin: "3rem 0",
        textAlign: "left",
        whiteSpace: "normal",
        wordBreak: "normal",
        overflowWrap: "break-word",
        lineHeight: "1.4",
    },
    syncStatusCard: {
        backgroundColor: "rgba(16, 26, 38, 0.92)",
        border: "1.5rem solid #38bdf8",
        borderRadius: "4rem",
        padding: "8rem 10rem",
        marginBottom: "8rem",
    },
    syncStatusHeader: {
        display: "flex",
        alignItems: "center",
        justifyContent: "space-between",
        fontSize: "12rem",
        fontWeight: "bold",
        letterSpacing: "0.5rem",
        color: "#38bdf8",
        textTransform: "uppercase",
        marginBottom: "4rem",
    },
    syncCompleteCard: {
        backgroundColor: "rgba(16, 26, 38, 0.92)",
        border: "1.5rem solid #05a065",
        borderRadius: "4rem",
        padding: "8rem 10rem",
        marginBottom: "8rem",
        display: "flex",
        alignItems: "center",
        color: "#10b981",
        fontSize: "12rem",
        fontWeight: "bold",
        letterSpacing: "0.3rem",
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
        backgroundColor: "rgba(10, 16, 24, 0.55)",
        border: "1.5rem solid rgba(157, 193, 222, 0.25)",
        borderRadius: "4rem",
        padding: "7rem 12rem",
    },
    sendButton: {
        marginLeft: "8rem",
        padding: "8rem 18rem",
        backgroundColor: "#05a065",
        border: "1.5rem solid #15c07b",
        color: "#ffffff",
        fontWeight: "bold",
        fontSize: "13.5rem",
        borderRadius: "4rem",
        letterSpacing: "0.5rem",
        textTransform: "uppercase",
    },
    footer: {
        display: "flex",
        justifyContent: "flex-end",
        flexShrink: 0,
    },
    footerButton: {
        marginLeft: "10rem",
        padding: "8rem 18rem",
        borderRadius: "4rem",
        fontWeight: "bold",
        fontSize: "13.5rem",
        letterSpacing: "0.5rem",
        textTransform: "uppercase",
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
    errorHelpButton: {
        marginTop: "7rem",
        padding: "4rem 10rem",
        fontSize: "11.5rem",
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
    playerLatency: {
        marginLeft: "auto",
        marginRight: "8rem",
        color: "#38bdf8",
        fontSize: "12rem",
        fontWeight: "bold",
    },
    playerBadge: {
        marginLeft: "6rem",
        color: "rgba(255, 255, 255, 0.62)",
        fontSize: "10.5rem",
        textTransform: "uppercase",
    },
    playerActionBtn: {
        marginLeft: "4rem",
        padding: "2rem 6rem",
        fontSize: "11rem",
        backgroundColor: "rgba(56, 189, 248, 0.15)",
        color: "#38bdf8",
        borderRadius: "2rem",
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

// ---- Panel body layout ----------------------------------------------------------

const PanelBody = ({ top, middle, bottom }: {
    top?: ReactNode;
    middle: ReactNode;
    bottom?: ReactNode;
}) => {
    return (
        <div style={styles.body}>
            {top ? <div style={styles.bodyTop}>{top}</div> : null}
            <div style={styles.bodyMiddle}>{middle}</div>
            {bottom ? <div style={styles.bodyBottom}>{bottom}</div> : null}
        </div>
    );
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
            }
        />
    );
};

// In-session settings behind the gear icon.
const SettingsView = () => {
    const t = useT();
    return (
        <PanelBody
            top={<div style={styles.lockedNote}>{t(LOC.lockedInSession, "Locked while a session is running.")}</div>}
            middle={
                <div style={styles.scrollArea}>
                    <SettingsFields />
                </div>
            }
        />
    );
};

const HostPlayerList = ({ players }: { players: PlayerEntry[] }) => {
    const t = useT();
    const isHost = useValue(isHost$);

    return (
        <div style={styles.playerSection}>
            <div style={styles.sectionHeader}>
                <span>{t(LOC.players, "Players")}</span>
                <span>{players.length}</span>
            </div>
            <div style={styles.playerList}>
                {players.map((player) => {
                    return (
                        <div key={player.id} style={styles.playerRow}>
                            <div style={styles.playerName}>{player.name}</div>
                            {player.latency !== undefined && player.latency >= 0 ? (
                                <span style={{
                                    display: "inline-flex",
                                    alignItems: "center",
                                    marginLeft: "auto",
                                    marginRight: "8rem",
                                    fontSize: "12rem",
                                    fontWeight: "bold",
                                    color: player.latency < 60 ? "#4ade80" : player.latency < 140 ? "#fbbf24" : "#f87171",
                                }}>
                                    <span style={{
                                        width: "6rem",
                                        height: "6rem",
                                        borderRadius: "50%",
                                        backgroundColor: player.latency < 60 ? "#4ade80" : player.latency < 140 ? "#fbbf24" : "#f87171",
                                        display: "inline-block",
                                        marginRight: "4rem",
                                    }} />
                                    {player.latency + " ms"}
                                </span>
                            ) : null}
                            {player.isHost ? (
                                <span style={styles.playerBadge}>{t(LOC.host, "Host")}</span>
                            ) : null}
                            {player.isYou ? (
                                <span style={styles.playerBadge}>{t(LOC.you, "You")}</span>
                            ) : null}
                            {!player.isYou ? (
                                <>
                                    <Button
                                        variant="flat"
                                        style={styles.playerActionBtn}
                                        onSelect={() => trigger(GROUP, "teleportToPlayer", player.id)}>
                                        Goto
                                    </Button>
                                    <Button
                                        variant="flat"
                                        style={styles.playerActionBtn}
                                        onSelect={() => trigger(GROUP, "followPlayer", player.id)}>
                                        Follow
                                    </Button>
                                </>
                            ) : null}
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

    // Escape leaves the dialog from anywhere in it, not only from the name field.
    useBackKey(onClose, !saving);

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
            default:
                break;
        }
    }

    const showProblemHelp = submitted && (saveStatus === "exists" || saveStatus === "unavailable");

    return (
        <Portal>
            <InputActionBarrier>
                <AutoNavigationScope
                    focusKey="cs2mp-save-dialog-scope"
                    debugName="CS2MP Save Dialog"
                    direction={NavigationDirection.Both}
                    initialFocused="name-input"
                    allowLooping>
                <BackConsumer onAction={onClose}>
                <div style={styles.saveDialogOverlay} onMouseDown={(e) => e.stopPropagation()}>
                    <div style={styles.saveDialogPanel}>
                        <div style={styles.saveDialogTitle}>{t(LOC.saveCopyTitle, "Save Local Copy")}</div>
                        <div style={styles.saveDialogHelp}>
                            {t(LOC.saveCopyBody,
                                "Save the current state of this host's world into your local saves folder so you can load it in singleplayer anytime.")}
                        </div>
                        <input
                            ref={inputRef}
                            type="text"
                            style={styles.saveDialogInput}
                            value={draft}
                            disabled={!canSave || saving || saved}
                            spellCheck={false}
                            autoComplete="off"
                            maxLength={85}
                            onChange={(event) => setDraft((event.target as HTMLInputElement).value)}
                            onMouseDown={(event) => event.stopPropagation()}
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
                                    {showProblemHelp ? (
                                        <OpenHelpButton
                                            page={HELP_PAGE.worldCopy}
                                            focusKey="save-help"
                                            style={styles.saveDialogButton}
                                        />
                                    ) : null}
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
                </BackConsumer>
                </AutoNavigationScope>
            </InputActionBarrier>
        </Portal>
    );
};

const renderCommandTokens = (cmdStr: string) => {
    const tokens = cmdStr.split(" ");
    return (
        <span style={{ whiteSpace: "nowrap" }}>
            {tokens.map((token, idx) => {
                const space = idx < tokens.length - 1 ? " " : "";
                if (token.startsWith("/")) {
                    return (
                        <span key={idx} style={{ color: "#38bdf8", fontWeight: "bold" }}>
                            {token}{space}
                        </span>
                    );
                }
                if (token.startsWith("<") || token.startsWith("[")) {
                    return (
                        <span key={idx} style={{ color: "#7dd3fc", fontStyle: "normal" }}>
                            {token}{space}
                        </span>
                    );
                }
                return (
                    <span key={idx} style={{ color: "#38bdf8" }}>
                        {token}{space}
                    </span>
                );
            })}
        </span>
    );
};

const renderColoredChatText = (rawText: string) => {
    if (!rawText) return null;
    const text = rawText.replace("[on|off]", "[on/off]");

    // 1. Headers like === Multiplayer Commands === or --- Host Commands ---
    if (text.startsWith("===") || text.startsWith("---")) {
        return (
            <div style={{
                color: "#facc15",
                fontWeight: "bold",
                fontSize: "12.5rem",
                letterSpacing: "0.6rem",
                margin: "8rem 0 4rem 0",
                paddingBottom: "2rem",
                borderBottom: "1rem solid rgba(250, 204, 21, 0.25)",
                display: "block",
            }}>
                {text}
            </div>
        );
    }

    // 2. Command listing line e.g. "- /ping [msg] - Ping map location..."
    if (text.startsWith("- /") || text.startsWith("/ping") || text.startsWith("/goto") || text.startsWith("/follow") ||
        text.startsWith("/unfollow") || text.startsWith("/sync") || text.startsWith("/clear") ||
        text.startsWith("/lock") || text.startsWith("/unlock") || text.startsWith("/motd") ||
        text.startsWith("/banlist") || text.startsWith("/unban")) {
        const clean = text.startsWith("- ") ? text.slice(2) : text;
        const firstDash = clean.indexOf(" - ");
        if (firstDash !== -1) {
            const cmd = clean.slice(0, firstDash);
            const desc = clean.slice(firstDash + 3);
            return (
                <div style={{
                    display: "flex",
                    flexDirection: "row",
                    alignItems: "baseline",
                    margin: "3rem 0",
                    lineHeight: "1.35",
                }}>
                    <span style={{ flexShrink: 0, whiteSpace: "nowrap" }}>
                        {renderCommandTokens(cmd)}
                    </span>
                    <span style={{ color: "rgba(255, 255, 255, 0.35)", margin: "0 6rem", flexShrink: 0 }}>
                        {"-"}
                    </span>
                    <span style={{ color: "#e2e8f0", flex: 1, minWidth: 0, wordBreak: "normal", overflowWrap: "break-word" }}>
                        {desc}
                    </span>
                </div>
            );
        }
    }

    // 3. Map Ping notifications: "[Ping] Pinged map at (X, Z)..." or "Pinged map at (X, Z)..."
    if (text.includes("Pinged map at")) {
        const cleanText = text.replace(/[\uD800-\uDBFF][\uDC00-\uDFFF]/g, "").trim();
        const coordMatch = cleanText.match(/\((-?[0-9]+),\s*(-?[0-9]+)\)/);
        if (coordMatch) {
            const before = cleanText.slice(0, coordMatch.index);
            const coords = coordMatch[0];
            const after = cleanText.slice((coordMatch.index || 0) + coords.length);
            return (
                <span>
                    <span style={{ color: "#f59e0b", fontWeight: "bold" }}>{"[Ping] "}</span>
                    <span style={{ color: "#e2e8f0" }}>{before.replace("[Ping]", "").trim() + " "}</span>
                    <span style={{ color: "#38bdf8", fontWeight: "bold" }}>{coords}</span>
                    <span style={{ color: "#e2e8f0" }}>{after}</span>
                </span>
            );
        }
    }

    // 4. Teleport / Follow camera notifications
    if (text.includes("Teleported camera") || text.includes("Now following") || text.includes("Stopped following")) {
        const cleanText = text.replace(/[\uD800-\uDBFF][\uDC00-\uDFFF]/g, "").replace("[Camera]", "").trim();
        return (
            <span>
                <span style={{ color: "#38bdf8", fontWeight: "bold" }}>{"[Camera] "}</span>
                <span style={{ color: "#e2e8f0" }}>{cleanText}</span>
            </span>
        );
    }

    // 5. General Chat with /commands or plain text
    const cleanText = text.replace(/[\uD800-\uDBFF][\uDC00-\uDFFF]/g, "");
    if (!cleanText.includes("/")) {
        return cleanText;
    }
    const words = cleanText.split(" ");
    return (
        <span>
            {words.map((word, wIdx) => {
                const space = wIdx < words.length - 1 ? " " : "";
                if (word.startsWith("/") && word.length > 1) {
                    return <span key={wIdx}><span style={{ color: "#38bdf8", fontWeight: "bold" }}>{word}</span>{space}</span>;
                }
                return word + space;
            })}
        </span>
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
    const statusKind = useValue(statusKind$);
    const canSaveClientWorld = useValue(canSaveClientWorld$);
    const [draft, setDraft] = useState("");
    const [typing, setTyping] = useState(false);
    const [saveDialogOpen, setSaveDialogOpen] = useState(false);
    const [history, setHistory] = useState<string[]>([]);
    const [historyIndex, setHistoryIndex] = useState<number>(-1);
    const draftBeforeHistoryRef = useRef<string>("");
    const listRef = useRef<HTMLDivElement | null>(null);
    const openedRef = useRef(true);

    const isSyncing = statusKind === "syncing" || progressMode !== "none";
    const [syncJustFinished, setSyncJustFinished] = useState(false);
    const wasSyncingRef = useRef(false);

    const AVAILABLE_COMMANDS = useMemo(() => [
        "/ping",
        "/goto",
        "/goto ping",
        "/follow",
        "/unfollow",
        "/sync",
        "/clear",
        "/help",
        "/lock",
        "/unlock",
        "/motd",
        "/banlist",
        "/unban",
    ], []);

    useEffect(() => {
        if (isSyncing) {
            wasSyncingRef.current = true;
        } else if (wasSyncingRef.current) {
            wasSyncingRef.current = false;
            setSyncJustFinished(true);
            const timer = window.setTimeout(() => setSyncJustFinished(false), 4000);
            return () => window.clearTimeout(timer);
        }
    }, [isSyncing]);

    // Keep the newest line in view (only auto-stick when already near the bottom,
    // so scrolling back through history is not yanked away by new messages).
    // Opening the panel always lands on the newest line, not on the oldest one.
    useEffect(() => {
        const el = listRef.current;
        if (!el) return;
        if (openedRef.current) {
            openedRef.current = false;
            el.scrollTop = el.scrollHeight;
            let frame = requestAnimationFrame(function toNewest() {
                const list = listRef.current;
                if (list) list.scrollTop = list.scrollHeight;
                frame = requestAnimationFrame(toNewest);
            });
            const stop = window.setTimeout(() => cancelAnimationFrame(frame), 300);
            return () => {
                cancelAnimationFrame(frame);
                window.clearTimeout(stop);
            };
        }
        const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 60;
        if (nearBottom) el.scrollTop = el.scrollHeight;
    }, [entries.length]);

    const send = () => {
        const text = draft.trim();
        if (!text) return;
        trigger(GROUP, "sendChat", text);
        setHistory((prev) => [...prev.filter((h) => h !== text), text]);
        setHistoryIndex(-1);
        setDraft("");
    };

    const activityPercent = isHost ? worldSendPercent : mapTransferPercent;

    const topBlock = (
        <>
            <HostPlayerList players={players} />

            {isSyncing ? (
                <div style={styles.syncStatusCard}>
                    <div style={styles.syncStatusHeader}>
                        <span>{"🔄 " + (statusTitle || t(LOC.syncWorld, "Syncing World..."))}</span>
                        {activityPercent >= 0 && progressMode === "percent" ? <span>{Math.round(activityPercent)}%</span> : null}
                    </div>
                    <TransferProgress
                        percent={activityPercent}
                        label=""
                        indeterminate={progressMode === "indeterminate" || activityPercent < 0}
                    />
                    {statusDetail ? <div style={styles.activityDetail}>{statusDetail}</div> : (
                        <div style={styles.activityDetail}>{"Synchronizing simulation state with all connected players..."}</div>
                    )}
                </div>
            ) : null}

            {syncJustFinished && !isSyncing ? (
                <div style={styles.syncCompleteCard}>
                    <span>{"✓ World in sync • All simulation state synchronized"}</span>
                </div>
            ) : null}
        </>
    );

    const filteredEntries = useMemo(() => {
        const result: ChatEntry[] = [];
        for (let i = 0; i < entries.length; i++) {
            const curr = entries[i];
            const prev = result[result.length - 1];
            if (curr.sender === null && prev && prev.sender === null && prev.text === curr.text) {
                continue;
            }
            result.push(curr);
        }
        return result;
    }, [entries]);

    const chatFeed = (
        <div ref={listRef} style={styles.chatList}>
            {filteredEntries.length === 0 ? (
                <div style={styles.chatEmpty}>{t(LOC.noMessages, "No messages yet.")}</div>
            ) : (
                filteredEntries.map((entry) =>
                    entry.sender === null ? (
                        <div key={entry.id} style={styles.systemLine}>{renderColoredChatText(entry.text)}</div>
                    ) : (
                        <div key={entry.id} style={styles.chatLine}>
                            <span style={styles.chatTime}>{entry.time + " "}</span>
                            <span style={styles.chatSender}>{entry.sender + ": "}</span>
                            <span>{renderColoredChatText(entry.text)}</span>
                        </div>
                    )
                )
            )}
        </div>
    );

    const bottomBlock = (
        <>
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
                            if (e.key === "Enter") {
                                send();
                            } else if (e.key === "Escape") {
                                e.preventDefault();
                                e.currentTarget.blur();
                            } else if (e.key === "ArrowUp") {
                                e.preventDefault();
                                if (history.length > 0) {
                                    if (historyIndex === -1) {
                                        draftBeforeHistoryRef.current = draft;
                                        const nextIdx = history.length - 1;
                                        setHistoryIndex(nextIdx);
                                        setDraft(history[nextIdx]);
                                    } else if (historyIndex > 0) {
                                        const nextIdx = historyIndex - 1;
                                        setHistoryIndex(nextIdx);
                                        setDraft(history[nextIdx]);
                                    }
                                }
                            } else if (e.key === "ArrowDown") {
                                e.preventDefault();
                                if (historyIndex !== -1) {
                                    if (historyIndex < history.length - 1) {
                                        const nextIdx = historyIndex + 1;
                                        setHistoryIndex(nextIdx);
                                        setDraft(history[nextIdx]);
                                    } else {
                                        setHistoryIndex(-1);
                                        setDraft(draftBeforeHistoryRef.current);
                                    }
                                }
                            } else if (e.key === "Tab") {
                                e.preventDefault();
                                if (draft.startsWith("/")) {
                                    const prefix = draft.toLowerCase();
                                    const match = AVAILABLE_COMMANDS.find((cmd) => cmd.toLowerCase().startsWith(prefix));
                                    if (match) {
                                        setDraft(match + " ");
                                    }
                                }
                            }
                        }}
                        onChange={(e) => {
                            setDraft((e.target as HTMLInputElement).value);
                            setHistoryIndex(-1);
                        }}
                    />
                </InputActionBarrier>
                <Button variant="primary" style={styles.sendButton} onSelect={send}>
                    {t(LOC.send, "Send")}
                </Button>
            </div>
            <div style={styles.footer}>
                <Button
                    variant="flat"
                    style={{
                        ...styles.footerButton,
                        backgroundColor: syncJustFinished ? "#05a065" : isSyncing ? "#0369a1" : "#223347",
                        border: syncJustFinished ? "1.5rem solid #15c07b" : isSyncing ? "1.5rem solid #38bdf8" : "1.5rem solid #3a5068",
                        color: "#ffffff",
                    }}
                    disabled={isSyncing}
                    onSelect={() => trigger(GROUP, "syncNow")}>
                    {isSyncing ? "Syncing..." : syncJustFinished ? "✓ Synced" : t(LOC.syncWorld, "Sync World")}
                </Button>
                <Button
                    variant="flat"
                    style={{
                        ...styles.footerButton,
                        backgroundColor: "#d33b5c",
                        border: "1.5rem solid #e44d6e",
                        color: "#ffffff",
                    }}
                    onSelect={() => trigger(GROUP, "requestDisconnect")}>
                    {isHost
                        ? t(LOC.closeSession, "Close Session")
                        : t(LOC.disconnect, "Disconnect")}
                </Button>
            </div>
        </>
    );

    return (
        <>
            {saveDialogOpen ? (
                <ClientWorldSaveDialog onClose={() => setSaveDialogOpen(false)} />
            ) : null}
            <PanelBody top={topBlock} middle={chatFeed} bottom={bottomBlock} />
        </>
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

const MIN_W = 320;
const MIN_H = 260;

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
        // The default position centres itself with a negative margin; an explicit
        // one must drop it or the panel sits half its height too high.
        panelStyle.marginTop = 0;
    }
    if (geometry.size) {
        panelStyle.width = geometry.size.w + "px";
        panelStyle.height = geometry.size.h + "px";
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
