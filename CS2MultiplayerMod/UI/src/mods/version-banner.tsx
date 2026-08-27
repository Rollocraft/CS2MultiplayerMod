import { bindValue, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { CSSProperties } from "react";
import { HELP_PAGE, OpenHelpButton } from "mods/help-link";

// Binding group shared with MultiplayerUISystem on the C# side.
const GROUP = "cs2mp";

const LOC = {
    title: "CS2MP.UI.VersionWarningTitle",
};

const useT = () => {
    const { translate } = useLocalization();
    return (id: string, fallback: string) => translate(id, fallback) ?? fallback;
};

// Localized warning sentence built C#-side (it interpolates the running and tested
// build numbers). Empty string when the build is tested, which hides the banner.
const versionWarning$ = bindValue<string>(GROUP, "versionWarning", "");

const styles: Record<string, CSSProperties> = {
    banner: {
        display: "flex",
        alignItems: "flex-start",
        padding: "10rem 12rem",
        marginBottom: "12rem",
        borderRadius: "3rem",
        backgroundColor: "rgba(255, 196, 87, 0.10)",
        border: "1rem solid rgba(255, 196, 87, 0.45)",
    },
    icon: {
        fontSize: "16rem",
        lineHeight: "1",
        marginRight: "10rem",
        flexShrink: 0,
        color: "#ffc457",
    },
    textWrap: {
        flex: 1,
    },
    title: {
        fontSize: "13rem",
        textTransform: "uppercase",
        letterSpacing: "0.5rem",
        color: "#ffc457",
        marginBottom: "2rem",
    },
    body: {
        fontSize: "12.5rem",
        lineHeight: "1.4",
        color: "rgba(255, 255, 255, 0.8)",
        wordBreak: "break-word",
    },
    helpButton: {
        marginTop: "7rem",
        padding: "4rem 10rem",
        fontSize: "11.5rem",
    },
};

// Non-blocking warning shown at the top of the Join dialog and the in-game hub
// whenever the running game build is not one the mod has been tested against.
// Renders nothing when the C# side reports an empty warning (tested build).
export const VersionWarningBanner = ({ style }: { style?: CSSProperties }) => {
    const t = useT();
    const warning = useValue(versionWarning$);
    if (!warning) return null;

    return (
        <div style={style ? { ...styles.banner, ...style } : styles.banner}>
            <div style={styles.icon}>⚠</div>
            <div style={styles.textWrap}>
                <div style={styles.title}>{t(LOC.title, "Untested Game Version")}</div>
                <div style={styles.body}>{warning}</div>
                <OpenHelpButton page={HELP_PAGE.gameVersion} style={styles.helpButton} />
            </div>
        </div>
    );
};
