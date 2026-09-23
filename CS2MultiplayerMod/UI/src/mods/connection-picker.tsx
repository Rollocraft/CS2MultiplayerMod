import { tryModule, useT } from "mods/ui-helpers";
import { Button, Dropdown, DropdownToggle } from "cs2/ui";
import { CSSProperties } from "react";

// Shared by the main-menu join/host screens and the in-game hub so all three
// offer the same choice and the same wording.
export const CONNECTION_RELAY = "relay";
export const CONNECTION_DIRECT = "direct";

export const CONNECTION_LOC = {
    mode: "CS2MP.Connection.Mode",
    relay: "CS2MP.Connection.Relay",
    direct: "CS2MP.Connection.Direct",
    relayHint: "CS2MP.Connection.RelayHint",
    directHint: "CS2MP.Connection.DirectHint",
    relayUnavailableHint: "CS2MP.Connection.RelayUnavailableHint",
    joinCode: "CS2MP.Connection.JoinCode",
    joinCodeHint: "CS2MP.Connection.JoinCodeHint",
    joinCodeUnavailable: "CS2MP.Connection.JoinCodeUnavailable",
    joinCodeSelectHint: "CS2MP.Connection.JoinCodeSelectHint",
    joinCodeEntry: "CS2MP.Connection.JoinCodeEntry",
    joinCodeEntryHint: "CS2MP.Connection.JoinCodeEntryHint",
};

// The menu's own dropdown skin, so this matches the native dropdowns elsewhere.
const dropdownTheme: Record<string, string> | undefined =
    tryModule("game-ui/menu/themes/dropdown.module.scss", "classes") ??
    tryModule("game-ui/common/input/dropdown/themes/default.module.scss", "classes") ??
    undefined;

// Resolved from the module registry rather than imported: "cs2/ui" exports the name
// DropdownItem for both the component and its props interface, and the type wins, so
// the import is unusable as a value. A plain button stands in if the path ever moves.
const VanillaDropdownItem = tryModule(
    "game-ui/common/input/dropdown/items/dropdown-item.tsx",
    "DropdownItem",
);

const fallbackOption: CSSProperties = {
    display: "block",
    width: "100%",
    padding: "9rem 14rem",
    fontSize: "17rem",
    textAlign: "left",
};

interface ConnectionDropdownProps {
    value: string;
    disabled?: boolean;
    style?: CSSProperties;
    onChange: (value: string) => void;
}

/**
 * Relay-or-direct selector. Relay is the default everywhere; direct is the
 * original address-and-port path.
 */
export const ConnectionDropdown = ({ value, disabled, style, onChange }: ConnectionDropdownProps) => {
    const t = useT();
    const relay = value !== CONNECTION_DIRECT;
    const relayLabel = t(CONNECTION_LOC.relay, "Steam Relay");
    const directLabel = t(CONNECTION_LOC.direct, "Direct Connection");

    const option = (optionValue: string, label: string, selected: boolean) =>
        VanillaDropdownItem ? (
            <VanillaDropdownItem
                key={optionValue}
                value={optionValue}
                theme={dropdownTheme}
                selected={selected}
                onChange={onChange}>
                {label}
            </VanillaDropdownItem>
        ) : (
            <Button
                key={optionValue}
                variant="menu"
                style={fallbackOption}
                onSelect={() => onChange(optionValue)}>
                {label}
            </Button>
        );

    const menu = (
        <>
            {option(CONNECTION_RELAY, relayLabel, relay)}
            {option(CONNECTION_DIRECT, directLabel, !relay)}
        </>
    );

    // A disabled picker still has to show which mode is running, so it renders as a
    // plain label rather than vanishing.
    if (disabled) {
        return (
            <div style={{ ...style, opacity: 0.55, padding: "9rem 0" }}>
                {relay ? relayLabel : directLabel}
            </div>
        );
    }

    return (
        <Dropdown theme={dropdownTheme} content={menu}>
            <DropdownToggle style={style}>{relay ? relayLabel : directLabel}</DropdownToggle>
        </Dropdown>
    );
};

const segment: CSSProperties = {
    flex: "1 1 0%",
    minWidth: 0,
    padding: "5rem 8rem",
    fontSize: "13rem",
    textAlign: "center",
    borderRadius: "3rem",
    border: "1rem solid rgba(157, 193, 222, 0.35)",
};

const segmentSelected: CSSProperties = {
    ...segment,
    backgroundColor: "rgba(157, 193, 222, 0.30)",
    color: "#ffffff",
};

const segmentIdle: CSSProperties = {
    ...segment,
    backgroundColor: "rgba(0, 0, 0, 0.35)",
    color: "#9dc1de",
};

/**
 * Same choice as ConnectionDropdown, laid out as two side-by-side buttons.
 * The in-game hub scrolls its body (overflowY: auto), which clips the dropdown's
 * anchored popup - so inside the hub the choice cannot use a popup at all.
 */
export const ConnectionSegmented = ({ value, disabled, onChange }: ConnectionDropdownProps) => {
    const t = useT();
    const relay = value !== CONNECTION_DIRECT;

    const seg = (optionValue: string, label: string, selected: boolean) => (
        <Button
            variant="flat"
            style={{
                ...(selected ? segmentSelected : segmentIdle),
                ...(disabled ? { opacity: 0.55 } : null),
                marginRight: optionValue === CONNECTION_RELAY ? "6rem" : 0,
            }}
            disabled={disabled}
            onSelect={() => {
                if (!selected) onChange(optionValue);
            }}>
            {label}
        </Button>
    );

    return (
        <div style={{ display: "flex", flex: 1, minWidth: 0 }}>
            {seg(CONNECTION_RELAY, t(CONNECTION_LOC.relay, "Steam Relay"), relay)}
            {seg(CONNECTION_DIRECT, t(CONNECTION_LOC.direct, "Direct"), !relay)}
        </div>
    );
};

/**
 * Read-only join code. The game exposes no clipboard API of any kind, so this
 * selects its whole contents when clicked and the player presses Ctrl+C - a real
 * Copy button could only ever lie about having worked.
 */
export const JoinCodeDisplay = ({ code, style }: { code: string; style?: CSSProperties }) => {
    const t = useT();
    return (
        <input
            readOnly
            style={style}
            value={code || t(CONNECTION_LOC.joinCodeUnavailable, "Unavailable")}
            spellCheck={false}
            autoComplete="off"
            onMouseDown={(e) => e.stopPropagation()}
            onFocus={(e) => (e.target as HTMLInputElement).select()}
            onClick={(e) => (e.target as HTMLInputElement).select()}
            onKeyDown={(e) => {
                if (e.key !== "Escape") e.stopPropagation();
            }}
        />
    );
};
