import { useT } from "mods/ui-helpers";
import { CSSProperties, useEffect, useState } from "react";

const LOC = {
    worldTransfer: "CS2MP.UI.WorldTransfer",
};

const styles: Record<string, CSSProperties> = {
    progress: {
        margin: "0 0 16rem 0",
    },
    progressHeader: {
        display: "flex",
        justifyContent: "space-between",
        alignItems: "center",
        fontSize: "14rem",
        color: "rgba(157, 193, 222, 0.9)",
        textTransform: "uppercase",
        marginBottom: "5rem",
    },
    progressTrack: {
        position: "relative",
        height: "9rem",
        backgroundColor: "rgba(0, 0, 0, 0.4)",
        border: "1rem solid rgba(157, 193, 222, 0.25)",
        borderRadius: "2rem",
        overflow: "hidden",
    },
    progressFill: {
        height: "100%",
        backgroundColor: "#72c8f0",
        boxShadow: "0 0 8rem rgba(114, 200, 240, 0.45)",
        transition: "width 160ms linear",
    },
    progressSweep: {
        position: "absolute",
        top: 0,
        bottom: 0,
        width: "30%",
        background:
            "linear-gradient(90deg, rgba(114,200,240,0) 0%, rgba(114,200,240,0.9) 50%, rgba(114,200,240,0) 100%)",
    },
};

const ProgressSweep = () => {
    const [position, setPosition] = useState(-30);

    useEffect(() => {
        let raf = 0;
        const tick = (time: number) => {
            setPosition(((time * 0.06) % 160) - 30);
            raf = requestAnimationFrame(tick);
        };
        raf = requestAnimationFrame(tick);
        return () => cancelAnimationFrame(raf);
    }, []);

    return <div style={{ ...styles.progressSweep, left: `${position}%` }} />;
};

// Compact progress presentation used inside the in-game multiplayer hub. The
// main-menu join form intentionally has no connection indicator; joining uses
// the blocking full-screen view instead.
export const TransferProgress = ({
    percent,
    label,
    indeterminate = false,
}: {
    percent: number;
    label?: string;
    indeterminate?: boolean;
}) => {
    // Hook must run unconditionally (before the early return) to keep hook order stable.
    const t = useT();
    if (percent < 0 && !indeterminate) return null;

    const clamped = Math.max(0, Math.min(100, Math.floor(percent)));
    return (
        <div style={styles.progress}>
            <div style={styles.progressHeader}>
                <span>{label ?? t(LOC.worldTransfer, "World Transfer")}</span>
                {!indeterminate ? <span>{clamped}%</span> : null}
            </div>
            <div style={styles.progressTrack}>
                {indeterminate
                    ? <ProgressSweep />
                    : <div style={{ ...styles.progressFill, width: `${clamped}%` }} />}
            </div>
        </div>
    );
};
