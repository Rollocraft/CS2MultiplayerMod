import { bindValue, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { CSSProperties } from "react";

// Binding group shared with MultiplayerUISystem on the C# side.
const GROUP = "cs2mp";

const LOC = {
    title: "CS2MP.UI.ModsBlockedTitle",
};

const useT = () => {
    const { translate } = useLocalization();
    return (id: string, fallback: string) => translate(id, fallback) ?? fallback;
};

// Localized sentence built C#-side (it names the offending mods). Empty when this mod
// is the only one live, which hides the banner and re-enables the host/join controls.
export const modsBlocked$ = bindValue<string>(GROUP, "modsBlocked", "");

/** Whether other mods are blocking multiplayer. Drives the disabled state of every
 *  control that would start a session. */
export const useModsBlocked = () => useValue(modsBlocked$).length > 0;

const styles: Record<string, CSSProperties> = {
    banner: {
        display: "flex",
        alignItems: "flex-start",
        padding: "10rem 12rem",
        marginBottom: "12rem",
        borderRadius: "3rem",
        backgroundColor: "rgba(255, 96, 88, 0.12)",
        border: "1rem solid rgba(255, 96, 88, 0.50)",
    },
    icon: {
        fontSize: "16rem",
        lineHeight: "1",
        marginRight: "10rem",
        flexShrink: 0,
        color: "#ff6058",
    },
    textWrap: {
        flex: 1,
    },
    title: {
        fontSize: "13rem",
        textTransform: "uppercase",
        letterSpacing: "0.5rem",
        color: "#ff6058",
        marginBottom: "2rem",
    },
    body: {
        fontSize: "12.5rem",
        lineHeight: "1.4",
        color: "rgba(255, 255, 255, 0.85)",
        wordBreak: "break-word",
    },
};

/**
 * Blocking notice shown on the Join dialog, the Host world picker and the in-game hub
 * whenever any mod other than this one is live in the active playset. Unlike the
 * untested-version banner this one is not advisory: the same binding disables the
 * controls beside it. Renders nothing when nothing else is running.
 */
export const OtherModsBanner = ({ style }: { style?: CSSProperties }) => {
    const t = useT();
    const blocked = useValue(modsBlocked$);
    if (!blocked) return null;

    return (
        <div style={style ? { ...styles.banner, ...style } : styles.banner}>
            <div style={styles.icon}>⛔</div>
            <div style={styles.textWrap}>
                <div style={styles.title}>{t(LOC.title, "Other Mods Enabled")}</div>
                <div style={styles.body}>{blocked}</div>
            </div>
        </div>
    );
};
