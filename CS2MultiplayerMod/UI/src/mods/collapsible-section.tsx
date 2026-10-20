import { CSSProperties, ReactNode, useEffect, useLayoutEffect, useRef, useState } from "react";

const ANIMATION_MS = 160;
const EASE = `${ANIMATION_MS}ms ease-out`;

const styles: Record<string, CSSProperties> = {
    section: {
        marginTop: "14rem",
        borderTop: "1rem solid rgba(157, 193, 222, 0.22)",
    },
    header: {
        display: "flex",
        alignItems: "center",
        paddingTop: "12rem",
        cursor: "pointer",
    },
    chevron: {
        width: "12rem",
        height: "12rem",
        marginRight: "10rem",
        filter: "brightness(0) invert(1)",
        opacity: 0.8,
        transition: `transform ${EASE}`,
    },
    title: {
        fontSize: "16rem",
        fontWeight: "bold",
        color: "#ffffff",
        textTransform: "uppercase",
        letterSpacing: "1rem",
    },
    titleCompact: {
        fontSize: "13rem",
        fontWeight: "bold",
        color: "#ffffff",
        textTransform: "uppercase",
        letterSpacing: "1rem",
    },
    content: {
        paddingTop: "12rem",
    },
};

/**
 * A titled group that folds away; the whole header toggles it. The body slides between zero and its
 * measured height, and is clipped only while moving so a dropdown inside can still open past it.
 */
export const CollapsibleSection = ({ title, open, onToggle, compact, children }: {
    title: string;
    open: boolean;
    onToggle: () => void;
    compact?: boolean;
    children: ReactNode;
}) => {
    const content = useRef<HTMLDivElement | null>(null);
    const [height, setHeight] = useState(0);
    const [mounted, setMounted] = useState(open);
    const [settled, setSettled] = useState(true);

    useEffect(() => {
        if (open) setMounted(true);
        setSettled(false);
        const timer = setTimeout(() => {
            setSettled(true);
            if (!open) setMounted(false);
        }, ANIMATION_MS);
        return () => clearTimeout(timer);
    }, [open]);

    // This runtime lays out a frame or two after a commit, so the height is read again then.
    useLayoutEffect(() => {
        const measure = () => {
            const measured = content.current ? content.current.offsetHeight : 0;
            if (measured > 0) setHeight(measured);
        };
        measure();
        let frame = requestAnimationFrame(() => {
            measure();
            frame = requestAnimationFrame(measure);
        });
        return () => cancelAnimationFrame(frame);
    });

    const body: CSSProperties = {
        // Unmeasured while opening: start from zero so the first open slides too.
        height: !open ? "0px" : height > 0 ? `${height}px` : settled ? "auto" : "0px",
        opacity: open ? 1 : 0,
        overflow: open && settled ? "visible" : "hidden",
        transition: `height ${EASE}, opacity ${EASE}`,
    };

    return (
        <div style={styles.section}>
            <div style={styles.header} onClick={onToggle}>
                <img
                    src="Media/Glyphs/ThickStrokeArrowRight.svg"
                    style={{ ...styles.chevron, transform: open ? "rotate(90deg)" : "rotate(0deg)" }}
                />
                <div style={compact ? styles.titleCompact : styles.title}>{title}</div>
            </div>
            <div style={body}>
                {mounted ? <div ref={content} style={styles.content}>{children}</div> : null}
            </div>
        </div>
    );
};
