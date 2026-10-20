import { CSSProperties, useEffect, useState } from "react";

const ring: CSSProperties = {
    width: "30rem",
    height: "30rem",
    flexShrink: 0,
    borderRadius: "50%",
    borderWidth: "3rem",
    borderStyle: "solid",
    borderColor: "rgba(255, 255, 255, 0.18)",
    borderTopColor: "#72c8f0",
};

// The UI runtime has no inline @keyframes, so the ring is turned from requestAnimationFrame.
export const Spinner = () => {
    const [angle, setAngle] = useState(0);

    useEffect(() => {
        let raf = 0;
        const tick = (time: number) => {
            setAngle((time * 0.36) % 360);
            raf = requestAnimationFrame(tick);
        };
        raf = requestAnimationFrame(tick);
        return () => cancelAnimationFrame(raf);
    }, []);

    return <div style={{ ...ring, transform: `rotate(${angle}deg)` }} />;
};
