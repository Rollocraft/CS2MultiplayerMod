import { bindValue, trigger, useValue } from "cs2/api";
import { AutoNavigationScope, BackConsumer, InputActionBarrier, NavigationDirection } from "cs2/input";
import { useLocalization } from "cs2/l10n";
import { Button, Portal } from "cs2/ui";
import { CSSProperties } from "react";
import { useBackKey } from "mods/back-action";

// Binding group shared with MultiplayerUISystem on the C# side.
const GROUP = "cs2mp";

// Locale keys served by the mod's LocaleEN/LocaleDE sources (L10n.Key constants).
const LOC = {
    title: "CS2MP.UI.DisclaimerTitle",
    body: "CS2MP.UI.DisclaimerBody",
    accept: "CS2MP.UI.DisclaimerAccept",
    decline: "CS2MP.UI.DisclaimerDecline",
};

const useT = () => {
    const { translate } = useLocalization();
    return (id: string, fallback: string) => translate(id, fallback) ?? fallback;
};

// True once the player has accepted the gate. Persisted in Setting, so it stays
// true across restarts and the modal only ever shows once.
export const disclaimerAccepted$ = bindValue<boolean>(GROUP, "disclaimerAccepted", false);

// rem behaves like resolution-independent pixels (the game scales root font size).
const styles: Record<string, CSSProperties> = {
    overlay: {
        position: "fixed",
        top: 0,
        left: 0,
        right: 0,
        bottom: 0,
        // Above the Join dialog overlay (zIndex 10000) so it gates that too.
        zIndex: 10001,
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        backgroundColor: "rgba(0, 0, 0, 0.6)",
        pointerEvents: "auto",
    },
    panel: {
        width: "480rem",
        maxWidth: "90%",
        backgroundColor: "rgba(24, 33, 51, 0.97)",
        borderRadius: "4rem",
        padding: "24rem",
        boxShadow: "0 16rem 48rem rgba(0, 0, 0, 0.45)",
        pointerEvents: "auto",
    },
    title: {
        fontSize: "24rem",
        color: "#ffffff",
        textTransform: "uppercase",
        marginBottom: "16rem",
    },
    body: {
        fontSize: "15rem",
        lineHeight: "1.45",
        color: "rgba(255, 255, 255, 0.85)",
        marginBottom: "22rem",
        wordBreak: "break-word",
    },
    buttons: {
        display: "flex",
        justifyContent: "flex-end",
    },
    button: {
        marginLeft: "12rem",
        padding: "8rem 20rem",
    },
};

// One-time acceptance gate. Rendered in place of the multiplayer UI until the
// player accepts; accepting fires the C# trigger that persists the flag, after
// which the disclaimerAccepted$ binding flips and the caller shows its real UI.
export const DisclaimerModal = ({ onAccept, onDecline }: {
    onAccept: () => void;
    onDecline: () => void;
}) => {
    const t = useT();
    // Escape backs out of the gate, exactly like its Cancel button.
    useBackKey(onDecline);

    return (
        <Portal>
            <InputActionBarrier>
                <AutoNavigationScope
                    debugName="CS2MP Disclaimer"
                    direction={NavigationDirection.Horizontal}
                    initialFocused="continue"
                    allowLooping>
                    <BackConsumer onAction={onDecline}>
                    <div style={styles.overlay} onMouseDown={(e) => e.stopPropagation()}>
                        <div style={styles.panel}>
                            <div style={styles.title}>{t(LOC.title, "Before You Continue")}</div>
                            <div style={styles.body}>
                                {t(LOC.body,
                                    "Multiplayer is experimental beta software, provided for free \"as is\". " +
                                    "Only host or join sessions with people you trust. By continuing you accept " +
                                    "that you use this mod at your own risk and that the author is not liable for " +
                                    "any damage, data loss, or other issues arising from its use, except where " +
                                    "liability cannot be excluded by law.")}
                            </div>
                            <div style={styles.buttons}>
                                <Button
                                    variant="primary"
                                    focusKey="continue"
                                    style={styles.button}
                                    onSelect={() => {
                                        trigger(GROUP, "acceptDisclaimer");
                                        onAccept();
                                    }}>
                                    {t(LOC.accept, "I Understand, Continue")}
                                </Button>
                                <Button focusKey="cancel" variant="flat" style={styles.button} onSelect={onDecline}>
                                    {t(LOC.decline, "Cancel")}
                                </Button>
                            </div>
                        </div>
                    </div>
                    </BackConsumer>
                </AutoNavigationScope>
            </InputActionBarrier>
        </Portal>
    );
};
