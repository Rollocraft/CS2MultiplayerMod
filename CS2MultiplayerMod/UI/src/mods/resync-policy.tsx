import { tryModule, useT } from "mods/ui-helpers";
import { Button, Dropdown, DropdownToggle } from "cs2/ui";
import { CSSProperties } from "react";

export const RESYNC_ALLOW = "allow";
export const RESYNC_APPROVAL = "approval";
export const RESYNC_HOST_ONLY = "hostOnly";

export const RESYNC_LOC = {
    policy: "CS2MP.UI.ResyncPolicy",
    allow: "CS2MP.UI.ResyncAllow",
    approval: "CS2MP.UI.ResyncApproval",
    hostOnly: "CS2MP.UI.ResyncHostOnly",
};

const dropdownTheme: Record<string, string> | undefined =
    tryModule("game-ui/menu/themes/dropdown.module.scss", "classes") ??
    tryModule("game-ui/common/input/dropdown/themes/default.module.scss", "classes") ??
    undefined;
const VanillaDropdownItem = tryModule(
    "game-ui/common/input/dropdown/items/dropdown-item.tsx",
    "DropdownItem",
);

interface ResyncPolicyProps {
    value: string;
    disabled?: boolean;
    style?: CSSProperties;
    onChange: (value: string) => void;
}

const labels = (t: ReturnType<typeof useT>): Record<string, string> => ({
    [RESYNC_ALLOW]: t(RESYNC_LOC.allow, "Allow"),
    [RESYNC_APPROVAL]: t(RESYNC_LOC.approval, "Ask Host"),
    [RESYNC_HOST_ONLY]: t(RESYNC_LOC.hostOnly, "Host Only"),
});

const normalized = (value: string) =>
    value === RESYNC_APPROVAL || value === RESYNC_HOST_ONLY ? value : RESYNC_ALLOW;

export const ResyncPolicyDropdown = ({ value, disabled, style, onChange }: ResyncPolicyProps) => {
    const t = useT();
    const selected = normalized(value);
    const text = labels(t);
    if (disabled) return <div style={{ ...style, opacity: 0.55 }}>{text[selected]}</div>;

    const option = (optionValue: string) => VanillaDropdownItem ? (
        <VanillaDropdownItem
            key={optionValue}
            value={optionValue}
            theme={dropdownTheme}
            selected={selected === optionValue}
            onChange={onChange}>
            {text[optionValue]}
        </VanillaDropdownItem>
    ) : (
        <Button
            key={optionValue}
            variant="menu"
            style={{ display: "block", width: "100%", padding: "9rem 14rem", textAlign: "left" }}
            onSelect={() => onChange(optionValue)}>
            {text[optionValue]}
        </Button>
    );

    return (
        <Dropdown theme={dropdownTheme} content={
            <>{option(RESYNC_ALLOW)}{option(RESYNC_APPROVAL)}{option(RESYNC_HOST_ONLY)}</>
        }>
            <DropdownToggle style={style}>{text[selected]}</DropdownToggle>
        </Dropdown>
    );
};

const segment: CSSProperties = {
    flex: "1 1 0%",
    minWidth: 0,
    padding: "5rem 5rem",
    fontSize: "12rem",
    textAlign: "center",
    borderRadius: "3rem",
    border: "1rem solid rgba(157, 193, 222, 0.35)",
};

export const ResyncPolicySegmented = ({ value, disabled, onChange }: ResyncPolicyProps) => {
    const t = useT();
    const selected = normalized(value);
    const text = labels(t);
    const options = [RESYNC_ALLOW, RESYNC_APPROVAL, RESYNC_HOST_ONLY];

    return (
        <div style={{ display: "flex", flex: 1, minWidth: 0 }}>
            {options.map((optionValue, index) => (
                <Button
                    key={optionValue}
                    variant="flat"
                    disabled={disabled}
                    style={{
                        ...segment,
                        marginRight: index < options.length - 1 ? "4rem" : 0,
                        backgroundColor: selected === optionValue
                            ? "rgba(157, 193, 222, 0.30)"
                            : "rgba(0, 0, 0, 0.35)",
                        color: selected === optionValue ? "#ffffff" : "#9dc1de",
                        opacity: disabled ? 0.55 : 1,
                    }}
                    onSelect={() => {
                        if (selected !== optionValue) onChange(optionValue);
                    }}>
                    {text[optionValue]}
                </Button>
            ))}
        </div>
    );
};
