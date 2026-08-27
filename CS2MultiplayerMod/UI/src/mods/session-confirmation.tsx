import { bindValue, trigger, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { ConfirmationDialog, DialogStack } from "cs2/ui";
import { useContext, useEffect, useRef } from "react";

const GROUP = "cs2mp";

const LOC = {
    closeTitle: "CS2MP.UI.CloseSessionTitle",
    closeBody: "CS2MP.UI.CloseSessionBody",
    closeSession: "CS2MP.UI.CloseSession",
    leaveTitle: "CS2MP.UI.LeaveSessionTitle",
    leaveBody: "CS2MP.UI.LeaveSessionBody",
    disconnect: "CS2MP.UI.Disconnect",
    cancel: "CS2MP.UI.Cancel",
};

const requested$ = bindValue<boolean>(GROUP, "disconnectConfirmationRequested", false);
const isHost$ = bindValue<boolean>(GROUP, "disconnectConfirmationIsHost", false);
const inGameWorld$ = bindValue<boolean>(GROUP, "inGameWorld", false);

type Surface = "menu" | "game";

/**
 * Turns a C# disconnect request (including one from the generated Options screen)
 * into the game's own centered confirmation dialog. Menu and Game hooks briefly
 * overlap during world changes, so exactly one surface owns the prompt.
 */
const SessionDisconnectConfirmation = ({ surface }: { surface: Surface }) => {
    const requested = useValue(requested$);
    const isHost = useValue(isHost$);
    const inGameWorld = useValue(inGameWorld$);
    const dialogStack = useContext(DialogStack);
    const shown = useRef(false);
    const { translate } = useLocalization();
    const t = (id: string, fallback: string) => translate(id, fallback) ?? fallback;
    const ownsSurface = surface === "game" ? inGameWorld : !inGameWorld;

    useEffect(() => {
        if (!requested) {
            shown.current = false;
            return;
        }
        if (!ownsSurface || shown.current) return;

        shown.current = true;
        try {
            dialogStack.showDialog(
                <ConfirmationDialog
                    title={isHost
                        ? t(LOC.closeTitle, "Close multiplayer session?")
                        : t(LOC.leaveTitle, "Leave multiplayer session?")}
                    message={isHost
                        ? t(
                            LOC.closeBody,
                            "This ends hosting and disconnects every other player. " +
                            "Are you sure you want to close the session?",
                        )
                        : t(
                            LOC.leaveBody,
                            "You will disconnect from the host. If you are playing in the downloaded shared city, " +
                            "the mod will return you to the main menu. Are you sure you want to leave?",
                        )}
                    confirm={isHost
                        ? t(LOC.closeSession, "Close Session")
                        : t(LOC.disconnect, "Disconnect")}
                    cancel={t(LOC.cancel, "Cancel")}
                    onConfirm={() => trigger(GROUP, "confirmDisconnect")}
                    onCancel={() => trigger(GROUP, "cancelDisconnect")}
                    dismissible={false}
                    cancellable
                />,
            );
        } catch (error) {
            shown.current = false;
            trigger(GROUP, "cancelDisconnect");
            console.warn("[cs2mp] Could not show the session-close confirmation dialog.", error);
        }
    }, [requested, isHost, ownsSurface, dialogStack, translate]);

    return null;
};

export const MenuSessionDisconnectConfirmation = () =>
    <SessionDisconnectConfirmation surface="menu" />;

export const GameSessionDisconnectConfirmation = () =>
    <SessionDisconnectConfirmation surface="game" />;
