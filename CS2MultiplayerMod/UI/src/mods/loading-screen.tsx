import { bindValue, trigger, useValue } from "cs2/api";
import { AutoNavigationScope, BackConsumer, InputActionBarrier, NavigationDirection } from "cs2/input";
import { useLocalization } from "cs2/l10n";
import { getModule } from "cs2/modding";
import { Button, Portal } from "cs2/ui";
import { CSSProperties, useEffect, useState } from "react";
import { useBackKey } from "mods/back-action";
import { MULTIPLAYER_BLUE } from "mods/multiplayer-theme";

// Binding group shared with MultiplayerUISystem on the C# side.
const GROUP = "cs2mp";

const LOC = {
    joiningTitle: "CS2MP.UI.JoiningTitle",
    multiplayer: "CS2MP.UI.Multiplayer",
    worldTransfer: "CS2MP.UI.WorldTransfer",
    loadingHint: "CS2MP.UI.LoadingHint",
    hostLoadingHint: "CS2MP.UI.HostLoadingHint",
    connectionFailed: "CS2MP.Status.ConnectionFailed",
    tryThis: "CS2MP.UI.TryThis",
    cancel: "CS2MP.UI.Cancel",
    close: "CS2MP.UI.Close",
    sessionEnded: "CS2MP.UI.SessionEnded",
    returningToMenu: "CS2MP.UI.ReturningToMenu",
    returningToMenuHint: "CS2MP.UI.ReturningToMenuHint",
    sharedWorldClosed: "CS2MP.UI.SharedWorldClosed",
    worldExitFailedTitle: "CS2MP.UI.WorldExitFailedTitle",
    worldExitFailed: "CS2MP.UI.WorldExitFailed",
    tryAgain: "CS2MP.UI.TryAgain",
};

const useT = () => {
    const { translate } = useLocalization();
    return (id: string, fallback: string) => translate(id, fallback) ?? fallback;
};

const statusKind$ = bindValue<string>(GROUP, "statusKind", "offline");
const statusTitle$ = bindValue<string>(GROUP, "statusTitle", "");
const statusDetail$ = bindValue<string>(GROUP, "statusDetail", "");
const statusHelp$ = bindValue<string>(GROUP, "statusHelp", "");
const progressMode$ = bindValue<string>(GROUP, "progressMode", "none");
const mapTransferPercent$ = bindValue<number>(GROUP, "mapTransferPercent", -1);
const worldSendPercent$ = bindValue<number>(GROUP, "worldSendPercent", -1);
const isHost$ = bindValue<boolean>(GROUP, "isHost", false);
const inGameWorld$ = bindValue<boolean>(GROUP, "inGameWorld", false);
const multiplayerMenuActive$ = bindValue<boolean>(GROUP, "multiplayerMenuActive", false);
const clientExitNoticeActive$ = bindValue<boolean>(GROUP, "clientExitNoticeActive", false);
const clientExitReturning$ = bindValue<boolean>(GROUP, "clientExitReturning", false);
const clientExitFailed$ = bindValue<boolean>(GROUP, "clientExitFailed", false);
const clientExitReason$ = bindValue<string>(GROUP, "clientExitReason", "");

type LoadingScreenSurface = "menu" | "game" | "multiplayer";

const tryModule = (path: string, exportName: string): any => {
    try {
        return getModule(path, exportName);
    } catch {
        return null;
    }
};

const backdropClasses: Record<string, string> | null =
    tryModule("game-ui/menu/components/menu-ui-backdrops/menu-ui-backdrops.module.scss", "classes");

// This is the same pool used by the vanilla main menu. "Backgound" is the
// spelling in the game's asset names, not a typo introduced here.
const FALLBACK_BACKDROPS = [1, 2, 3, 4, 5, 6, 7]
    .map((n) => `Media/Menu/Backdrops/Backgound0${n}.png`);

const currentMenuBackdropImage = (): string => {
    try {
        const className = backdropClasses?.backdropImage?.split(/\s+/)[0];
        if (className) {
            const elements = document.getElementsByClassName(className);
            // The newest element is the visible one while vanilla cross-fades.
            for (let i = elements.length - 1; i >= 0; i--) {
                const element = elements[i] as HTMLElement;
                const image = element.style.backgroundImage || getComputedStyle(element).backgroundImage;
                if (image && image !== "none") return image;
            }
        }
    } catch {
        // The menu can already be unmounting; use the native/static pool below.
    }

    const nativeList = tryModule(
        "game-ui/menu/components/menu-ui-backdrops/menu-ui-backdrops.tsx",
        "BACKDROPS_LIST",
    );
    const list: string[] = Array.isArray(nativeList) && nativeList.length > 0
        ? nativeList
        : FALLBACK_BACKDROPS;
    return `url('${list[Math.floor(Math.random() * list.length)]}')`;
};

// Menu and Game roots overlap briefly during a world transition. Share one captured
// image across those mounts so the artwork does not jump midway through loading.
let sharedBackdropImage: string | null = null;
let sharedBackdropUsers = 0;
let sharedBackdropClearTimer: number | null = null;

const acquireBackdropImage = (): string => {
    sharedBackdropUsers++;
    if (sharedBackdropClearTimer !== null) {
        window.clearTimeout(sharedBackdropClearTimer);
        sharedBackdropClearTimer = null;
    }
    if (!sharedBackdropImage) sharedBackdropImage = currentMenuBackdropImage();
    return sharedBackdropImage;
};

const releaseBackdropImage = () => {
    sharedBackdropUsers = Math.max(0, sharedBackdropUsers - 1);
    if (sharedBackdropUsers !== 0) return;
    sharedBackdropClearTimer = window.setTimeout(() => {
        if (sharedBackdropUsers === 0) sharedBackdropImage = null;
        sharedBackdropClearTimer = null;
    }, 500);
};

// rem behaves like resolution-independent pixels (the game scales root font size).
const styles: Record<string, CSSProperties> = {
    overlay: {
        position: "fixed",
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        // Above every mod panel and dialog so it reads as a real loading screen.
        zIndex: 99999,
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
        overflow: "hidden",
        // Fallback while the main-menu city image is being resolved.
        backgroundColor: MULTIPLAYER_BLUE,
        pointerEvents: "auto",
    },
    backdrop: {
        position: "absolute",
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        zIndex: 0,
        backgroundPosition: "center",
        backgroundSize: "cover",
        backgroundRepeat: "no-repeat",
        pointerEvents: "none",
    },
    backdropDim: {
        position: "absolute",
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        zIndex: 0,
        backgroundColor: "rgba(11, 16, 27, 0.60)",
        pointerEvents: "none",
    },
    content: {
        position: "relative",
        zIndex: 1,
        width: "100%",
        height: "100%",
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
    },
    title: {
        fontSize: "44rem",
        fontWeight: "bold",
        letterSpacing: "2rem",
        textTransform: "uppercase",
        color: "#ffffff",
        marginBottom: "40rem",
        textShadow: "0 2rem 16rem rgba(114, 200, 240, 0.35)",
    },
    barOuter: {
        width: "640rem",
        maxWidth: "85%",
    },
    barHeader: {
        display: "flex",
        justifyContent: "space-between",
        alignItems: "center",
        marginBottom: "8rem",
    },
    phase: {
        fontSize: "17rem",
        textTransform: "uppercase",
        letterSpacing: "1rem",
        color: "#9dc1de",
    },
    percent: {
        fontSize: "17rem",
        color: "#72c8f0",
        fontWeight: "bold",
    },
    track: {
        position: "relative",
        height: "12rem",
        backgroundColor: "rgba(0, 0, 0, 0.5)",
        border: "1rem solid rgba(157, 193, 222, 0.30)",
        borderRadius: "3rem",
        overflow: "hidden",
    },
    fill: {
        height: "100%",
        backgroundColor: "#72c8f0",
        boxShadow: "0 0 14rem rgba(114, 200, 240, 0.6)",
        transition: "width 180ms linear",
    },
    // Indeterminate sweep: a highlight slides across the empty track.
    sweep: {
        position: "absolute",
        top: 0,
        bottom: 0,
        width: "30%",
        background:
            "linear-gradient(90deg, rgba(114,200,240,0) 0%, rgba(114,200,240,0.85) 50%, rgba(114,200,240,0) 100%)",
    },
    detail: {
        marginTop: "14rem",
        fontSize: "14rem",
        color: "rgba(255, 255, 255, 0.6)",
        minHeight: "18rem",
        textAlign: "center",
    },
    hint: {
        marginTop: "6rem",
        fontSize: "13rem",
        color: "rgba(255, 255, 255, 0.4)",
        textAlign: "center",
    },
    error: {
        width: "640rem",
        maxWidth: "85%",
        padding: "22rem 26rem",
        backgroundColor: "rgba(24, 33, 51, 0.92)",
        borderLeft: "4rem solid #ff8a7a",
        borderRadius: "4rem",
        marginBottom: "10rem",
        textAlign: "left",
    },
    errorTitle: {
        fontSize: "22rem",
        color: "#ff9c8f",
        fontWeight: "bold",
        marginBottom: "10rem",
    },
    errorSummary: {
        fontSize: "16rem",
        color: "#ffffff",
        lineHeight: "1.45",
    },
    helpTitle: {
        marginTop: "18rem",
        marginBottom: "5rem",
        color: "#9dc1de",
        fontSize: "14rem",
        fontWeight: "bold",
        textTransform: "uppercase",
    },
    errorHelp: {
        fontSize: "14rem",
        color: "rgba(255, 255, 255, 0.76)",
        maxWidth: "640rem",
        lineHeight: "1.45",
    },
    cancel: {
        marginTop: "40rem",
        padding: "9rem 28rem",
    },
};

// Animated indeterminate bar (connecting / loading, before a byte count exists).
// The game's UI runtime has no inline @keyframes, so the sweep is positioned from
// requestAnimationFrame, like the join dialog's spinner.
const IndeterminateBar = () => {
    const [pos, setPos] = useState(-30);

    useEffect(() => {
        let raf = 0;
        const tick = (time: number) => {
            // 0..130 then wrap, so the 30%-wide sweep travels fully off both ends.
            setPos(((time * 0.06) % 160) - 30);
            raf = requestAnimationFrame(tick);
        };
        raf = requestAnimationFrame(tick);
        return () => cancelAnimationFrame(raf);
    }, []);

    return (
        <div style={styles.track}>
            <div style={{ ...styles.sweep, left: `${pos}%` }} />
        </div>
    );
};

// Blocking full-screen state shared by host world synchronization and every
// client join phase. A client sees it immediately after pressing Join, including
// the time spent waiting for manual host approval, through world transfer/load.
export const JoinLoadingScreen = ({ surface }: { surface: LoadingScreenSurface }) => {
    const t = useT();
    const statusKind = useValue(statusKind$);
    const statusTitle = useValue(statusTitle$);
    const statusDetail = useValue(statusDetail$);
    const statusHelp = useValue(statusHelp$);
    const progressMode = useValue(progressMode$);
    const mapTransferPercent = useValue(mapTransferPercent$);
    const worldSendPercent = useValue(worldSendPercent$);
    const isHost = useValue(isHost$);
    const inGameWorld = useValue(inGameWorld$);
    const multiplayerMenuActive = useValue(multiplayerMenuActive$);
    const clientExitNoticeActive = useValue(clientExitNoticeActive$);
    const clientExitReturning = useValue(clientExitReturning$);
    const clientExitFailed = useValue(clientExitFailed$);
    const clientExitReason = useValue(clientExitReason$);
    const percent = isHost ? worldSendPercent : mapTransferPercent;

    // The native Multiplayer sub-screen is the most reliable owner while a player
    // starts a join. Outside it, select exactly one root hook. Menu and Game may both
    // be mounted for a few frames during a world replacement, so rendering from both
    // would create overlapping input barriers and duplicate focus keys.
    const ownsSurface = surface === "multiplayer"
        ? multiplayerMenuActive && !inGameWorld
        : surface === "game"
            ? inGameWorld
            : !multiplayerMenuActive && !inGameWorld;

    // Shown from the first "connecting" until connected/offline. An error keeps it
    // up (so the failure is visible) until the player dismisses it.
    const [active, setActive] = useState(false);
    useEffect(() => {
        if (statusKind === "error") {
            setActive(true);
        } else if (statusKind === "syncing") {
            setActive(true);
        } else if (isHost) {
            setActive(false);
        } else if (statusKind === "connecting") {
            setActive(true);
        } else if (statusKind === "connected" || statusKind === "offline" || statusKind === "disabled") {
            setActive(false);
        }
    }, [statusKind, isHost]);

    const overlayVisible = active || clientExitNoticeActive;
    const [backdropImage, setBackdropImage] = useState<string | null>(null);
    useEffect(() => {
        if (!overlayVisible) {
            setBackdropImage(null);
            return;
        }

        setBackdropImage(acquireBackdropImage());
        return releaseBackdropImage;
    }, [overlayVisible]);

    const failed = statusKind === "error";
    const synchronizing = statusKind === "syncing";
    const dismiss = () => {
        setActive(false);
        // Clear the faulted session so the next attempt starts clean, and drop the
        // remembered fault with it: the status is re-read on every mount, so a fault
        // left standing puts this same screen back up on the next visit.
        trigger(GROUP, "disconnect");
        trigger(GROUP, "dismissStatusFault");
    };
    const dismissClientExit = () => {
        setActive(false);
        trigger(GROUP, "dismissClientExitNotice");
    };
    const retryClientExit = () => trigger(GROUP, "retryClientWorldExit");

    // What Escape (and the gamepad's Back) does here is whatever the screen's own
    // button does. While it is mid-work there is nothing to go back to: a return to
    // the menu runs to completion, and a failed exit only offers its retry.
    const returningToMenu = clientExitNoticeActive && clientExitReturning;
    const cancellable = !failed && !clientExitNoticeActive && (!synchronizing || !isHost);
    const backAction = failed
        ? dismiss
        : clientExitNoticeActive
            ? (!returningToMenu && !clientExitFailed ? dismissClientExit : null)
            : (cancellable ? dismiss : null);
    // Focus is what puts this overlay's input barrier and Back handler into the input
    // stack; without it the game keeps every shortcut while the screen blocks it.
    const focusedButton = failed
        ? "dismiss"
        : clientExitNoticeActive
            ? (returningToMenu ? undefined : "exit-notice")
            : (cancellable ? "cancel" : undefined);

    useBackKey(
        backAction ?? (() => {}),
        ownsSurface && overlayVisible && backAction !== null,
    );

    if (!ownsSurface || !overlayVisible) return null;

    const phaseTitle = statusTitle || t(LOC.joiningTitle, "Joining Multiplayer Game");
    const clamped = Math.max(0, Math.min(100, Math.floor(percent)));
    const determinate = progressMode === "determinate" && percent >= 0;

    return (
        <Portal>
            <InputActionBarrier>
                <AutoNavigationScope
                    debugName="CS2MP Connection Screen"
                    direction={NavigationDirection.Both}
                    initialFocused={focusedButton}
                    allowLooping>
                <BackConsumer
                    disabled={backAction === null}
                    onAction={() => { if (backAction !== null) backAction(); }}>
                <div style={styles.overlay}>
                    {backdropImage ? (
                        <>
                            <div style={{ ...styles.backdrop, backgroundImage: backdropImage }} />
                            <div style={styles.backdropDim} />
                        </>
                    ) : null}

                    <div style={styles.content}>
                        <div style={styles.title}>{t(LOC.multiplayer, "Multiplayer")}</div>

                        {clientExitNoticeActive ? (
                            clientExitReturning ? (
                                <div style={styles.barOuter}>
                                    <div style={styles.barHeader}>
                                        <span style={styles.phase}>
                                            {t(LOC.returningToMenu, "Returning to the main menu")}
                                        </span>
                                    </div>
                                    <IndeterminateBar />
                                    <div style={styles.detail}>{clientExitReason}</div>
                                    <div style={styles.hint}>
                                        {t(
                                            LOC.returningToMenuHint,
                                            "The disconnected shared city is being closed so you cannot keep editing its temporary copy.",
                                        )}
                                    </div>
                                </div>
                            ) : (
                                <>
                                    <div style={{
                                        ...styles.error,
                                        borderLeftColor: clientExitFailed ? "#ff8a7a" : "#72c8f0",
                                    }}>
                                        <div style={{
                                            ...styles.errorTitle,
                                            color: clientExitFailed ? "#ff9c8f" : "#9dc1de",
                                        }}>
                                            {clientExitFailed
                                                ? t(LOC.worldExitFailedTitle, "Could not close the shared city")
                                                : t(LOC.sessionEnded, "Multiplayer session ended")}
                                        </div>
                                        {clientExitReason ? (
                                            <div style={styles.errorSummary}>{clientExitReason}</div>
                                        ) : null}
                                        {clientExitFailed ? (
                                            <div style={styles.helpTitle}>{t(LOC.tryThis, "Try this")}</div>
                                        ) : null}
                                        <div style={styles.errorHelp}>
                                            {clientExitFailed
                                                ? t(
                                                    LOC.worldExitFailed,
                                                    "The game did not accept the automatic return. The temporary world has not been deleted while it is open. Try again; if this keeps failing, close the game instead of continuing in this disconnected copy.",
                                                )
                                                : t(
                                                    LOC.sharedWorldClosed,
                                                    "The shared city was closed automatically. Its downloaded copy is temporary; the host owns the session save, and changes made after the connection ended cannot be sent back.",
                                                )}
                                        </div>
                                    </div>
                                    <Button
                                        variant="primary"
                                        focusKey="exit-notice"
                                        style={styles.cancel}
                                        onSelect={clientExitFailed ? retryClientExit : dismissClientExit}
                                    >
                                        {clientExitFailed
                                            ? t(LOC.tryAgain, "Try again")
                                            : t(LOC.close, "Close")}
                                    </Button>
                                </>
                            )
                        ) : failed ? (
                            <>
                                <div style={styles.error}>
                                    <div style={styles.errorTitle}>
                                        {statusTitle || t(LOC.connectionFailed, "Connection failed")}
                                    </div>
                                    {statusDetail ? <div style={styles.errorSummary}>{statusDetail}</div> : null}
                                    {statusHelp ? (
                                        <>
                                            <div style={styles.helpTitle}>{t(LOC.tryThis, "Try this")}</div>
                                            <div style={styles.errorHelp}>{statusHelp}</div>
                                        </>
                                    ) : null}
                                </div>
                                <Button
                                    variant="primary"
                                    focusKey="dismiss"
                                    style={styles.cancel}
                                    onSelect={dismiss}>
                                    {t(LOC.close, "Close")}
                                </Button>
                            </>
                        ) : (
                            <>
                                <div style={styles.barOuter}>
                                    <div style={styles.barHeader}>
                                        <span style={styles.phase}>{phaseTitle}</span>
                                        {determinate ? <span style={styles.percent}>{clamped}%</span> : null}
                                    </div>
                                    {determinate ? (
                                        <div style={styles.track}>
                                            <div style={{ ...styles.fill, width: `${clamped}%` }} />
                                        </div>
                                    ) : (
                                        <IndeterminateBar />
                                    )}
                                    <div style={styles.detail}>{statusDetail}</div>
                                    <div style={styles.hint}>
                                        {isHost
                                            ? t(LOC.hostLoadingHint, "The city will resume when every player is ready.")
                                            : t(LOC.loadingHint, "Keep this window open while the host's city is transferred.")}
                                    </div>
                                </div>
                                {cancellable ? (
                                    <Button
                                        variant="flat"
                                        focusKey="cancel"
                                        style={styles.cancel}
                                        onSelect={dismiss}>
                                        {t(LOC.cancel, "Cancel")}
                                    </Button>
                                ) : null}
                            </>
                        )}
                    </div>
                </div>
                </BackConsumer>
                </AutoNavigationScope>
            </InputActionBarrier>
        </Portal>
    );
};

// Prop-free wrappers satisfy the game's append-hook component contract while
// keeping surface ownership explicit.
export const MenuJoinLoadingScreen = () => <JoinLoadingScreen surface="menu" />;
export const GameJoinLoadingScreen = () => <JoinLoadingScreen surface="game" />;
export const MultiplayerJoinLoadingScreen = () => <JoinLoadingScreen surface="multiplayer" />;
