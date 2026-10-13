import { useLocalization } from "cs2/l10n";
import { getModule } from "cs2/modding";

// Binding group of MultiplayerUISystem. Field values live in the mod's Setting, so the join dialog,
// the in-game hub and Options share them.
export const GROUP = "cs2mp";

// The translation binding can return null before a locale loads.
export const useT = () => {
    const { translate } = useLocalization();
    return (id: string, fallback: string) => translate(id, fallback) ?? fallback;
};

// Vanilla modules can move between game versions; callers supply their own fallback.
export const tryModule = (path: string, exportName: string): any => {
    try {
        return getModule(path, exportName);
    } catch {
        return null;
    }
};

export const parseArray = <T>(json: string): T[] => {
    try {
        const parsed = JSON.parse(json);
        return Array.isArray(parsed) ? parsed : [];
    } catch {
        return [];
    }
};
