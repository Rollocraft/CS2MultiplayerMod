import { trigger } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { Button } from "cs2/ui";
import { CSSProperties } from "react";

const GROUP = "cs2mp";

export const HELP_PAGE = {
    errors: "errors-and-warnings.md",
    gameVersion: "troubleshooting.md#game-version-issues",
    mods: "mods.md",
    sharedWorldExit: "errors-and-warnings.md#could-not-close-the-shared-city",
    worldCopy: "errors-and-warnings.md#world-copy-errors",
} as const;

export const OpenHelpButton = ({ page, style, focusKey }: {
    page: string;
    style?: CSSProperties;
    focusKey?: string;
}) => {
    const { translate } = useLocalization();
    const label = translate("CS2MP.UI.OpenHelp", "Open Help") ?? "Open Help";

    if (!page) return null;

    return (
        <Button
            variant="flat"
            focusKey={focusKey}
            style={style}
            onSelect={() => trigger(GROUP, "openHelp", page)}>
            {label}
        </Button>
    );
};
