import { CSSProperties, ReactElement, useEffect, useState } from "react";

export interface FormFieldProps {
    label: string;
    value: string;
    secret?: boolean;
    disabled?: boolean;
    onChange: (value: string) => void;
}

interface Options {
    // Needs row, label, input and inputDisabled.
    styles: Record<string, CSSProperties>;
    // Lets Escape reach the native Back handler; every other key stays out of game shortcuts.
    escapeBubbles?: boolean;
    // Wraps the input, e.g. in an InputActionBarrier while it is being edited.
    wrap?: (input: ReactElement, editing: boolean) => ReactElement;
}

// Commits every keystroke but only re-syncs from the binding while not being edited.
export const FormField = ({ label, value, secret, disabled, onChange, styles, escapeBubbles, wrap }: FormFieldProps & Options) => {
    const [draft, setDraft] = useState(value);
    const [editing, setEditing] = useState(false);

    useEffect(() => {
        if (!editing) setDraft(value);
    }, [value]);

    const input = (
        <input
            type={secret ? "password" : "text"}
            style={disabled ? { ...styles.input, ...styles.inputDisabled } : styles.input}
            value={draft}
            disabled={disabled}
            spellCheck={false}
            autoComplete="off"
            onFocus={() => setEditing(true)}
            onBlur={() => {
                setEditing(false);
                if (draft !== value) onChange(draft);
            }}
            onMouseDown={(e) => e.stopPropagation()}
            onKeyDown={(e) => {
                if (!escapeBubbles || e.key !== "Escape") e.stopPropagation();
            }}
            onChange={(e) => {
                const next = (e.target as HTMLInputElement).value;
                setDraft(next);
                onChange(next);
            }}
        />
    );

    return (
        <div style={styles.row}>
            <div style={styles.label}>{label}</div>
            {wrap ? wrap(input, editing) : input}
        </div>
    );
};
