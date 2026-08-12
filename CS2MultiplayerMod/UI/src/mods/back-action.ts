import { useEffect, useRef } from "react";

/**
 * Escape closes a blocking overlay.
 *
 * The game turns a window-level Escape into its own "Back" action, and the
 * BackConsumer that would receive it is only in the input stack while something
 * inside it holds focus - an overlay nothing focused therefore ignored Escape and
 * let it open the pause menu instead. Reading the key here covers that case; the
 * capture phase runs before the game's own window handler, so the key is consumed
 * rather than acted on twice.
 */
export const useBackKey = (onBack: () => void, enabled: boolean = true) => {
    const handler = useRef(onBack);
    useEffect(() => {
        handler.current = onBack;
    }, [onBack]);

    useEffect(() => {
        if (!enabled) return;
        const onKeyDown = (event: KeyboardEvent) => {
            if (event.key !== "Escape" && event.keyCode !== 27) return;
            event.stopPropagation();
            event.preventDefault();
            handler.current();
        };
        window.addEventListener("keydown", onKeyDown, true);
        return () => window.removeEventListener("keydown", onKeyDown, true);
    }, [enabled]);
};
