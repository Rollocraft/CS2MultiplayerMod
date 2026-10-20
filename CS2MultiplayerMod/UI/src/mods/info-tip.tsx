import { Tooltip } from "cs2/ui";
import { CSSProperties } from "react";

const styles: Record<string, CSSProperties> = {
    anchor: {
        display: "flex",
        alignItems: "center",
        flexShrink: 0,
        marginLeft: "8rem",
        marginRight: "8rem",
    },
    icon: {
        width: "18rem",
        height: "18rem",
        filter: "brightness(0) invert(1)",
        opacity: 0.7,
    },
    body: {
        maxWidth: "420rem",
        whiteSpace: "normal",
    },
};

// A long explanation stays out of the layout until the icon is hovered. Clicks stop here so the
// option row it sits in does not toggle.
export const InfoTip = ({ text }: { text: string }) => (
    <Tooltip tooltip={<div style={styles.body}>{text}</div>} direction="right">
        <div style={styles.anchor} onClick={(e) => e.stopPropagation()}>
            <img src="Media/Glyphs/Info.svg" style={styles.icon} />
        </div>
    </Tooltip>
);
