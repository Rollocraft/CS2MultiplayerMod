import { bindValue, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { CSSProperties } from "react";
import { HELP_PAGE, OpenHelpButton } from "mods/help-link";

// Binding group shared with MultiplayerUISystem on the C# side.
const GROUP = "cs2mp";

const LOC = {
    title: "CS2MP.UI.ModsBlockedTitle",
    ignoredTitle: "CS2MP.UI.ModsIgnoredTitle",
};

const useT = () => {
    const { translate } = useLocalization();
    return (id: string, fallback: string) => translate(id, fallback) ?? fallback;
};

// Localized sentence built C#-side (it names the other mods). Empty when this mod
// is the only one live. With the own-risk override it remains visible as a warning.
export const modsBlocked$ = bindValue<string>(GROUP, "modsBlocked", "");
const modsCheckIgnored$ = bindValue<boolean>(GROUP, "modsCheckIgnored", false);

/** Whether other mods are blocking multiplayer. The own-risk override turns the same
 *  detection into an advisory notice instead. */
export const useModsBlocked = () => {
    const notice = useValue(modsBlocked$);
    const ignored = useValue(modsCheckIgnored$);
    return notice.length > 0 && !ignored;
};

const styles: Record<string, CSSProperties> = {
    banner: {
        display: "flex",
        alignItems: "flex-start",
        padding: "10rem 12rem",
        marginBottom: "12rem",
        borderRadius: "3rem",
        // On the main menu this sits straight over the animated scene, so the tint
        // needs an opaque ground of its own or the text reads through to it.
        backgroundColor: "rgba(38, 12, 11, 0.94)",
        border: "1rem solid rgba(255, 96, 88, 0.60)",
    },
    icon: {
        width: "16rem",
        height: "16rem",
        marginTop: "1rem",
        marginRight: "10rem",
        flexShrink: 0,
        // The UI font has no glyph for the emoji symbols, so tint the game's own
        // warning glyph the way its tinted-icon component does.
        maskImage: "url(Media/Glyphs/Warning.svg)",
        maskSize: "contain",
        maskPosition: "center",
        maskRepeat: "no-repeat",
        backgroundColor: "#ff6058",
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
        color: "rgba(255, 255, 255, 0.92)",
        wordBreak: "break-word",
    },
    helpButton: {
        marginTop: "7rem",
        padding: "4rem 10rem",
        fontSize: "11.5rem",
    },
};

/**
 * Notice shown on the Join dialog, Host world picker and in-game hub whenever any mod
 * other than this one is live. It blocks by default and becomes an explicit own-risk
 * warning when the compatibility override is enabled.
 */
export const OtherModsBanner = ({ style }: { style?: CSSProperties }) => {
    const t = useT();
    const blocked = useValue(modsBlocked$);
    const ignored = useValue(modsCheckIgnored$);
    if (!blocked) return null;

    return (
        <div style={style ? { ...styles.banner, ...style } : styles.banner}>
            <div style={styles.icon} />
            <div style={styles.textWrap}>
                <div style={styles.title}>
                    {ignored
                        ? t(LOC.ignoredTitle, "Compatibility Check Ignored")
                        : t(LOC.title, "Other Mods Enabled")}
                </div>
                <div style={styles.body}>{blocked}</div>
                <OpenHelpButton page={HELP_PAGE.mods} style={styles.helpButton} />
            </div>
        </div>
    );
};
