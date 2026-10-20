import { GROUP, parseArray, useT } from "mods/ui-helpers";
import { bindValue, trigger, useValue } from "cs2/api";
import { AutoNavigationScope, NavigationDirection } from "cs2/input";
import { Button, Tooltip } from "cs2/ui";
import { CSSProperties, ReactNode, useEffect, useMemo, useRef, useState } from "react";
import { FormField, FormFieldProps } from "mods/form-field";
import { InfoTip } from "mods/info-tip";
import { MULTIPLAYER_BLUE } from "mods/multiplayer-theme";
import { Spinner } from "mods/spinner";
import classes from "./server-browser.module.scss";
import searchIcon from "mods/images/cs2mp-search.svg";
import blockedIcon from "mods/images/cs2mp-blocked.svg";
import messageIcon from "mods/images/cs2mp-message.svg";
import sadIcon from "mods/images/cs2mp-sad.svg";

export const BROWSER_LOC = {
    publicLobbies: "CS2MP.UI.PublicLobbies",
    allServers: "CS2MP.UI.AllServers",
    browseHint: "CS2MP.UI.BrowseHint",
    refresh: "CS2MP.UI.Refresh",
    search: "CS2MP.UI.SearchServers",
    filterFreeSlots: "CS2MP.UI.FilterFreeSlots",
    filterNoPassword: "CS2MP.UI.FilterNoPassword",
    filterCompatible: "CS2MP.UI.FilterCompatible",
    noMatches: "CS2MP.UI.NoMatches",
    searching: "CS2MP.UI.Searching",
    noServers: "CS2MP.UI.NoServers",
    noServersHint: "CS2MP.UI.NoServersHint",
    contacting: "CS2MP.UI.Contacting",
    serverPlayers: "CS2MP.UI.ServerPlayers",
    serverPassword: "CS2MP.UI.ServerPassword",
    serverAsksMessage: "CS2MP.UI.ServerAsksMessage",
    serverFull: "CS2MP.UI.ServerFull",
    serverIncompatible: "CS2MP.UI.ServerIncompatible",
    unnamedServer: "CS2MP.UI.UnnamedServer",
    joinMessage: "CS2MP.UI.JoinMessage",
    joinMessageHint: "CS2MP.UI.JoinMessageHint",
    browseUnavailable: "CS2MP.UI.BrowseUnavailable",
    password: "CS2MP.UI.Password",
    join: "CS2MP.UI.Join",
    cancel: "CS2MP.UI.Cancel",
};

const serverList$ = bindValue<string>(GROUP, "serverList", "[]");
const serverListState$ = bindValue<string>(GROUP, "serverListState", "idle");
const serverListError$ = bindValue<string>(GROUP, "serverListError", "");
const relayAvailable$ = bindValue<boolean>(GROUP, "relayAvailable", false);
const relayUnavailableReason$ = bindValue<string>(GROUP, "relayUnavailableReason", "");
const maxJoinMessageLength$ = bindValue<number>(GROUP, "maxJoinMessageLength", 160);

const MAX_SEARCH_LENGTH = 48;

// One search per this long; PublicListing.cs guards a little under it.
const REFRESH_COOLDOWN_MS = 5000;
// The arrow turns into the loading ring over this long...
const MORPH_MS = 250;
// ...and the ring stays at least this long, or for as long as the search runs.
const MIN_SPIN_MS = 800;

// Built C#-side from sanitized lobby data; rendered as text only.
interface ListedServer {
    id: string;
    name: string;
    players: number;
    max: number;
    password: boolean;
    message: boolean;
    compatible: boolean;
    version: string;
    // Steam's local estimate through the relay; absent while unknown.
    ping?: number;
}

const SUBTLE = "#b7c3d3";
const MUTED = "rgba(255, 255, 255, 0.4)";
const LINE = "rgba(255, 255, 255, 0.13)";
const GOOD = "#3ddc84";
const FAIR = "#f5b94a";
const POOR = "#f26b6b";

const styles: Record<string, CSSProperties> = {
    area: {
        height: "100%",
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
    },
    // Reserved under the panel so the panel does not move when progress appears.
    progressSlot: {
        height: "64rem",
        marginTop: "16rem",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
    },
    progress: {
        display: "flex",
        alignItems: "center",
        padding: "12rem 22rem",
        borderRadius: "32rem",
        backgroundColor: "rgba(0, 0, 0, 0.55)",
        boxShadow: "0 8rem 24rem rgba(0, 0, 0, 0.35)",
    },
    progressText: {
        marginLeft: "14rem",
        fontSize: "18rem",
        color: "#ffffff",
    },
    panel: {
        width: "1240rem",
        maxWidth: "92%",
        backgroundColor: MULTIPLAYER_BLUE,
        border: "1rem solid rgba(255, 255, 255, 0.1)",
        borderRadius: "12rem",
        padding: "28rem 32rem",
        boxShadow: "0 16rem 48rem rgba(0, 0, 0, 0.45)",
        pointerEvents: "auto",
    },
    header: {
        display: "flex",
        alignItems: "center",
        marginBottom: "20rem",
    },
    titleRow: {
        flex: "1 1 0%",
        minWidth: 0,
        display: "flex",
        alignItems: "center",
    },
    title: {
        fontSize: "34rem",
        fontWeight: "bold",
        color: "#ffffff",
    },
    toolbar: {
        display: "flex",
        alignItems: "center",
        marginBottom: "16rem",
    },
    search: {
        flex: "1 1 0%",
        minWidth: 0,
        height: "52rem",
        display: "flex",
        alignItems: "center",
        padding: "0 16rem",
        backgroundColor: "rgba(0, 0, 0, 0.25)",
        border: `1rem solid ${LINE}`,
        borderRadius: "6rem",
    },
    searchField: {
        flex: "1 1 0%",
        minWidth: 0,
        height: "100%",
        position: "relative",
        display: "flex",
        alignItems: "center",
        marginLeft: "12rem",
    },
    searchInput: {
        width: "100%",
        fontSize: "18rem",
        color: "#ffffff",
        backgroundColor: "transparent",
        borderStyle: "none",
    },
    // Drawn by hand: the input's own placeholder does not show in the game UI.
    searchPlaceholder: {
        position: "absolute",
        left: 0,
        top: 0,
        bottom: 0,
        display: "flex",
        alignItems: "center",
        fontSize: "18rem",
        color: MUTED,
        pointerEvents: "none",
    },
    chip: {
        display: "flex",
        alignItems: "center",
        flexShrink: 0,
        marginLeft: "28rem",
    },
    chipBox: {
        width: "22rem",
        height: "22rem",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        marginRight: "12rem",
        border: "1rem solid rgba(255, 255, 255, 0.35)",
        borderRadius: "4rem",
    },
    chipBoxOn: {
        width: "22rem",
        height: "22rem",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        marginRight: "12rem",
        backgroundColor: "#12a865",
        border: "1rem solid #12a865",
        borderRadius: "4rem",
    },
    chipLabel: {
        marginLeft: "8rem",
        fontSize: "17rem",
        color: "#ffffff",
    },
    refreshFace: {
        position: "relative",
        width: "30rem",
        height: "30rem",
    },
    refreshLayer: {
        position: "absolute",
        left: 0,
        top: 0,
        width: "100%",
        height: "100%",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
    },
    warning: {
        fontSize: "15rem",
        lineHeight: "1.35",
        color: FAIR,
        marginBottom: "12rem",
        wordBreak: "break-word",
    },
    list: {
        height: "540rem",
        overflowY: "auto",
    },
    empty: {
        padding: "40rem 0",
        textAlign: "center",
        fontSize: "17rem",
        color: "rgba(255, 255, 255, 0.6)",
    },
    nobody: {
        height: "100%",
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
        textAlign: "center",
    },
    nobodyTitle: {
        marginTop: "24rem",
        fontSize: "24rem",
        fontWeight: "bold",
        color: "rgba(255, 255, 255, 0.8)",
    },
    nobodyHint: {
        marginTop: "10rem",
        fontSize: "17rem",
        color: MUTED,
    },
    row: {
        display: "flex",
        alignItems: "center",
        padding: "14rem 18rem",
        marginBottom: "8rem",
        backgroundColor: "rgba(255, 255, 255, 0.035)",
        border: "1rem solid rgba(255, 255, 255, 0.07)",
        borderRadius: "8rem",
    },
    rowText: {
        flex: "1 1 0%",
        minWidth: 0,
        paddingRight: "16rem",
    },
    nameRow: {
        display: "flex",
        alignItems: "center",
    },
    name: {
        flexShrink: 1,
        minWidth: 0,
        fontSize: "22rem",
        fontWeight: "bold",
        color: "#ffffff",
        overflow: "hidden",
        textOverflow: "ellipsis",
        whiteSpace: "nowrap",
    },
    meta: {
        display: "flex",
        flexWrap: "wrap",
        alignItems: "center",
        marginTop: "6rem",
        fontSize: "16rem",
        color: SUBTLE,
    },
    metaItem: {
        display: "flex",
        alignItems: "center",
        marginRight: "22rem",
    },
    full: {
        flexShrink: 0,
        padding: "3rem 12rem",
        marginLeft: "14rem",
        fontSize: "14rem",
        color: "#ffb3b3",
        backgroundColor: "rgba(220, 53, 53, 0.28)",
        border: "1rem solid rgba(242, 107, 107, 0.55)",
        borderRadius: "14rem",
    },
    stats: {
        width: "120rem",
        flexShrink: 0,
        display: "flex",
        flexDirection: "column",
        alignItems: "flex-end",
        marginRight: "24rem",
    },
    ping: {
        display: "flex",
        alignItems: "center",
        fontSize: "17rem",
    },
    // About the text's cap height, so the bars read as part of the number.
    bars: {
        height: "13rem",
        display: "flex",
        alignItems: "flex-end",
        marginRight: "5rem",
    },
    bar: {
        width: "3rem",
        marginLeft: "2rem",
        borderRadius: "1rem",
    },
    version: {
        marginTop: "10rem",
        fontSize: "16rem",
        color: SUBTLE,
    },
    formTitle: {
        fontSize: "26rem",
        fontWeight: "bold",
        color: "#ffffff",
        marginBottom: "18rem",
        overflow: "hidden",
        textOverflow: "ellipsis",
        whiteSpace: "nowrap",
    },
    // FormField needs row, label, input and inputDisabled.
    fieldRow: {
        display: "flex",
        alignItems: "center",
        marginBottom: "12rem",
    },
    label: {
        width: "200rem",
        fontSize: "17rem",
        color: SUBTLE,
    },
    input: {
        flex: 1,
        fontSize: "18rem",
        color: "#ffffff",
        backgroundColor: "rgba(0, 0, 0, 0.25)",
        border: `1rem solid ${LINE}`,
        borderRadius: "6rem",
        padding: "11rem 14rem",
    },
    inputDisabled: {
        opacity: 0.55,
    },
    fieldHint: {
        fontSize: "14rem",
        lineHeight: "1.35",
        color: SUBTLE,
        margin: "-4rem 0 12rem 200rem",
    },
    buttons: {
        display: "flex",
        alignItems: "center",
        justifyContent: "flex-end",
        marginTop: "20rem",
    },
    cancel: {
        padding: "10rem 28rem",
        fontSize: "17rem",
        marginRight: "12rem",
    },
};

const fieldStyles: Record<string, CSSProperties> = { ...styles, row: styles.fieldRow };

const Field = (props: FormFieldProps) => <FormField {...props} styles={fieldStyles} escapeBubbles />;

// Tints a game or bundled glyph.
const Glyph = ({ src, size, color, gap }: { src: string; size: number; color: string; gap?: number }) => (
    <div style={{
        width: `${size}rem`,
        height: `${size}rem`,
        flexShrink: 0,
        marginRight: gap ? `${gap}rem` : undefined,
        maskImage: `url(${src})`,
        maskSize: "contain",
        maskPosition: "center",
        maskRepeat: "no-repeat",
        backgroundColor: color,
    }} />
);

const Meta = ({ icon, color, children }: { icon: string; color?: string; children: string }) => (
    <div style={color ? { ...styles.metaItem, color } : styles.metaItem}>
        <Glyph src={icon} size={16} color={color ?? SUBTLE} gap={8} />
        {children}
    </div>
);

const FilterChip = ({ icon, label, value, onChange }: {
    icon: string;
    label: string;
    value: boolean;
    onChange: (value: boolean) => void;
}) => (
    <div className={classes.chip} style={styles.chip} onClick={() => onChange(!value)}>
        <div style={value ? styles.chipBoxOn : styles.chipBox}>
            {value ? <Glyph src="Media/Glyphs/Checkmark.svg" size={14} color="#ffffff" /> : null}
        </div>
        <Glyph src={icon} size={18} color="#ffffff" />
        <div style={styles.chipLabel}>{label}</div>
    </div>
);

const BAR_HEIGHTS = [5, 9, 13];

const Ping = ({ ping }: { ping?: number }) => {
    const known = ping !== undefined && ping >= 0;
    const lit = !known ? 0 : ping < 80 ? 3 : ping < 150 ? 2 : 1;
    const color = lit === 3 ? GOOD : lit === 2 ? FAIR : lit === 1 ? POOR : MUTED;
    return (
        <div style={styles.ping}>
            <div style={styles.bars}>
                {BAR_HEIGHTS.map((height, i) => (
                    <div key={i} style={{
                        ...styles.bar,
                        height: `${height}rem`,
                        backgroundColor: i < lit ? color : "rgba(255, 255, 255, 0.18)",
                    }} />
                ))}
            </div>
            <div style={{ color }}>{known ? `${ping} ms` : "–"}</div>
        </div>
    );
};

const JoinButton = ({ blocked, disabled, focusKey, onSelect, children }: {
    blocked: boolean;
    disabled: boolean;
    focusKey?: string;
    onSelect: () => void;
    children: ReactNode;
}) => (
    <Button
        variant="flat"
        theme={{ button: classes.join }}
        className={disabled ? classes.joinDisabled : undefined}
        focusKey={focusKey}
        disabled={disabled}
        onSelect={() => {
            if (!disabled) onSelect();
        }}>
        <Glyph
            src={blocked ? blockedIcon : "Media/Glyphs/SimulationPlay.svg"}
            size={18}
            color={disabled ? "rgba(255, 255, 255, 0.55)" : "#ffffff"}
            gap={12}
        />
        {children}
    </Button>
);

// On press the arrow turns into a loading ring while the search runs, then sits dimmed until the next
// search is allowed. Its own component, so the animation re-renders only the button.
const RefreshButton = ({ label, disabled, searching, onRefresh }: {
    label: string;
    disabled: boolean;
    searching: boolean;
    onRefresh: () => void;
}) => {
    // Showing the list counts as a press: it searches, and the cooldown starts.
    const [pressedAt, setPressedAt] = useState(() => performance.now());
    const [elapsed, setElapsed] = useState(0);
    const searchingRef = useRef(searching);
    searchingRef.current = searching;

    useEffect(() => {
        onRefresh();
    }, []);

    useEffect(() => {
        if (pressedAt < 0) return;
        let raf = 0;
        let timer = 0;
        const tick = () => {
            const t = performance.now() - pressedAt;
            setElapsed(t);
            if (t < MIN_SPIN_MS || searchingRef.current) raf = requestAnimationFrame(tick);
            else timer = window.setTimeout(() => setPressedAt(-1), Math.max(0, REFRESH_COOLDOWN_MS - t));
        };
        raf = requestAnimationFrame(tick);
        return () => {
            cancelAnimationFrame(raf);
            window.clearTimeout(timer);
        };
    }, [pressedAt]);

    const active = pressedAt >= 0;
    const spinning = active && (elapsed < MIN_SPIN_MS || searching);
    // 0 = arrow, 1 = ring.
    const morph = Math.min(1, elapsed / MORPH_MS);

    return (
        <Tooltip tooltip={label} direction="down">
            <Button
                variant="flat"
                theme={{ button: classes.refresh }}
                className={disabled || (active && !spinning) ? classes.refreshDisabled : undefined}
                focusKey="refresh"
                disabled={disabled}
                onSelect={() => {
                    if (disabled || active) return;
                    setElapsed(0);
                    setPressedAt(performance.now());
                    onRefresh();
                }}>
                <div style={styles.refreshFace}>
                    {spinning ? (
                        <div style={{ ...styles.refreshLayer, opacity: morph }}>
                            <Spinner />
                        </div>
                    ) : null}
                    {!spinning || morph < 1 ? (
                        <div style={spinning ? {
                            ...styles.refreshLayer,
                            opacity: 1 - morph,
                            transform: `rotate(${morph * 270}deg)`,
                        } : styles.refreshLayer}>
                            <Glyph src="Media/Glyphs/ArrowCircular.svg" size={28} color="#ffffff" />
                        </div>
                    ) : null}
                </div>
            </Button>
        </Tooltip>
    );
};

const fill = (template: string, ...values: (string | number)[]) =>
    values.reduce<string>((text, value, i) => text.split(`{${i}}`).join(String(value)), template);

const isFull = (server: ListedServer) => server.max > 0 && server.players >= server.max;

/**
 * Sessions other players listed on Steam. Joining asks Steam for the listing's owner and connects to that
 * account over the relay only, so no address is exchanged in either direction.
 */
export const ServerBrowser = ({ focusKey }: { focusKey: number | string }) => {
    const t = useT();
    const serverListJson = useValue(serverList$);
    const state = useValue(serverListState$);
    const error = useValue(serverListError$);
    const relayAvailable = useValue(relayAvailable$);
    const relayReason = useValue(relayUnavailableReason$);
    const maxMessage = useValue(maxJoinMessageLength$);
    const servers = useMemo(() => parseArray<ListedServer>(serverListJson), [serverListJson]);
    const [selected, setSelected] = useState<ListedServer | null>(null);
    const [password, setPassword] = useState("");
    const [message, setMessage] = useState("");
    const [query, setQuery] = useState("");
    const [freeSlotsOnly, setFreeSlotsOnly] = useState(true);
    const [noPasswordOnly, setNoPasswordOnly] = useState(false);
    const [compatibleOnly, setCompatibleOnly] = useState(false);

    const busy = state === "joining";
    const serverName = (server: ListedServer) => server.name || t(BROWSER_LOC.unnamedServer, "Unnamed City");
    const join = (server: ListedServer, pw: string, note: string) =>
        trigger(GROUP, "joinListedServer", server.id, pw, note.trim());

    const needle = query.trim().toLowerCase();
    const shown = servers.filter((server) =>
        (needle.length === 0 || serverName(server).toLowerCase().indexOf(needle) >= 0) &&
        (!freeSlotsOnly || !isFull(server)) &&
        (!noPasswordOnly || !server.password) &&
        (!compatibleOnly || server.compatible));

    const choose = (server: ListedServer) => {
        if (!server.password && !server.message) {
            join(server, "", "");
            return;
        }
        setPassword("");
        setMessage("");
        setSelected(server);
    };

    const warning = !relayAvailable
        ? `${t(BROWSER_LOC.browseUnavailable, "Steam is not available right now, so Public Lobbies cannot be shown. Launch the game through Steam.")}${relayReason ? ` (${relayReason})` : ""}`
        : error;
    const statusLines = warning ? <div style={styles.warning}>{warning}</div> : null;
    // The join's progress sits outside the panel.
    const progress = (
        <div style={styles.progressSlot}>
            {busy ? (
                <div style={styles.progress}>
                    <Spinner />
                    <div style={styles.progressText}>
                        {t(BROWSER_LOC.contacting, "Contacting the host through Steam...")}
                    </div>
                </div>
            ) : null}
        </div>
    );

    if (selected) {
        const missing = (selected.password && password.length === 0) ||
            (selected.message && message.trim().length === 0);
        return (
            <div style={styles.area}>
                <AutoNavigationScope
                    focusKey={focusKey}
                    debugName="CS2MP Listed Join"
                    direction={NavigationDirection.Both}
                    initialFocused="listed-join"
                    allowLooping>
                    <div style={styles.panel} onMouseDown={(e) => e.stopPropagation()}>
                        <div style={styles.formTitle}>{serverName(selected)}</div>
                        {statusLines}
                        {selected.password ? (
                            <Field
                                label={t(BROWSER_LOC.password, "Password")}
                                secret
                                value={password}
                                disabled={busy}
                                onChange={setPassword}
                            />
                        ) : null}
                        {selected.message ? (
                            <>
                                <Field
                                    label={t(BROWSER_LOC.joinMessage, "Message to Host")}
                                    value={message}
                                    disabled={busy}
                                    maxLength={maxMessage}
                                    onChange={setMessage}
                                />
                                <div style={styles.fieldHint}>
                                    {t(BROWSER_LOC.joinMessageHint, "The host asks why you would like to join. They read it before letting you in.")}
                                    {` (${message.length}/${maxMessage})`}
                                </div>
                            </>
                        ) : null}
                        <div style={styles.buttons}>
                            <Button variant="flat" style={styles.cancel} onSelect={() => setSelected(null)}>
                                {t(BROWSER_LOC.cancel, "Cancel")}
                            </Button>
                            <JoinButton
                                blocked={false}
                                disabled={busy || missing}
                                focusKey="listed-join"
                                onSelect={() => join(selected, password, message)}>
                                {t(BROWSER_LOC.join, "Join")}
                            </JoinButton>
                        </div>
                    </div>
                </AutoNavigationScope>
                {progress}
            </div>
        );
    }

    return (
        <div style={styles.area}>
            <AutoNavigationScope
                focusKey={focusKey}
                debugName="CS2MP Public Lobbies"
                direction={NavigationDirection.Vertical}
                initialFocused="refresh"
                allowLooping>
                <div style={styles.panel} onMouseDown={(e) => e.stopPropagation()}>
                    <div style={styles.header}>
                        <div style={styles.titleRow}>
                            <div style={styles.title}>{t(BROWSER_LOC.allServers, "All Lobbies")}</div>
                            <InfoTip text={t(BROWSER_LOC.browseHint, "Cities other players listed on Steam. You join through the Steam relay, so neither you nor the host sees the other's IP address.")} />
                        </div>
                        <RefreshButton
                            label={t(BROWSER_LOC.refresh, "Refresh")}
                            disabled={!relayAvailable || busy}
                            searching={state === "searching"}
                            onRefresh={() => trigger(GROUP, "searchServers")}
                        />
                    </div>
                    <div style={styles.toolbar}>
                        <div style={styles.search}>
                            <Glyph src={searchIcon} size={22} color={SUBTLE} />
                            <div style={styles.searchField}>
                                {query.length === 0 ? (
                                    <div style={styles.searchPlaceholder}>{t(BROWSER_LOC.search, "Search lobbies...")}</div>
                                ) : null}
                                <input
                                    type="text"
                                    style={styles.searchInput}
                                    value={query}
                                    spellCheck={false}
                                    autoComplete="off"
                                    onMouseDown={(e) => e.stopPropagation()}
                                    // Escape still reaches the native Back handler; other keys stay out of shortcuts.
                                    onKeyDown={(e) => {
                                        if (e.key !== "Escape") e.stopPropagation();
                                    }}
                                    onChange={(e) =>
                                        setQuery((e.target as HTMLInputElement).value.slice(0, MAX_SEARCH_LENGTH))}
                                />
                            </div>
                        </div>
                        <FilterChip
                            icon="Media/Glyphs/Passenger.svg"
                            label={t(BROWSER_LOC.filterFreeSlots, "Free Slots")}
                            value={freeSlotsOnly}
                            onChange={setFreeSlotsOnly}
                        />
                        <FilterChip
                            icon="Media/Glyphs/Lock.svg"
                            label={t(BROWSER_LOC.filterNoPassword, "No Password")}
                            value={noPasswordOnly}
                            onChange={setNoPasswordOnly}
                        />
                        <FilterChip
                            icon="Media/Glyphs/Gear.svg"
                            label={t(BROWSER_LOC.filterCompatible, "Same Version")}
                            value={compatibleOnly}
                            onChange={setCompatibleOnly}
                        />
                    </div>
                    {statusLines}
                    <div style={styles.list}>
                        {servers.length === 0 ? (
                            <div style={styles.nobody}>
                                {state === "searching" ? (
                                    <div style={styles.nobodyHint}>{t(BROWSER_LOC.searching, "Searching for lobbies...")}</div>
                                ) : (
                                    <>
                                        <Glyph src={sadIcon} size={96} color={MUTED} />
                                        <div style={styles.nobodyTitle}>
                                            {t(BROWSER_LOC.noServers, "Sorry, no lobbies are online right now.")}
                                        </div>
                                        <div style={styles.nobodyHint}>
                                            {t(BROWSER_LOC.noServersHint, "A host shows up here once they turn on List Publicly.")}
                                        </div>
                                    </>
                                )}
                            </div>
                        ) : shown.length === 0 ? (
                            <div style={styles.empty}>
                                {t(BROWSER_LOC.noMatches, "No lobbies match your search or filters.")}
                            </div>
                        ) : shown.map((server) => {
                            const full = isFull(server);
                            const blocked = full || !server.compatible;
                            return (
                                <div key={server.id} className={classes.row} style={styles.row}>
                                    <div style={styles.rowText}>
                                        <div style={styles.nameRow}>
                                            <div style={styles.name}>{serverName(server)}</div>
                                            {full ? <div style={styles.full}>{t(BROWSER_LOC.serverFull, "Full")}</div> : null}
                                        </div>
                                        <div style={styles.meta}>
                                            <Meta icon="Media/Glyphs/Passenger.svg">
                                                {fill(t(BROWSER_LOC.serverPlayers, "{0}/{1} players"), server.players, server.max)}
                                            </Meta>
                                            {server.password ? (
                                                <Meta icon="Media/Glyphs/Lock.svg">
                                                    {t(BROWSER_LOC.serverPassword, "Password")}
                                                </Meta>
                                            ) : (
                                                <Meta icon="Media/Glyphs/OpenLock.svg">
                                                    {t(BROWSER_LOC.filterNoPassword, "No Password")}
                                                </Meta>
                                            )}
                                            {server.message ? (
                                                <Meta icon={messageIcon}>
                                                    {t(BROWSER_LOC.serverAsksMessage, "Asks for a message")}
                                                </Meta>
                                            ) : null}
                                            {!server.compatible ? (
                                                <Meta icon="Media/Glyphs/Warning.svg" color={FAIR}>
                                                    {t(BROWSER_LOC.serverIncompatible, "Different game or mod version")}
                                                </Meta>
                                            ) : null}
                                        </div>
                                    </div>
                                    <div style={styles.stats}>
                                        <Ping ping={server.ping} />
                                        <div style={server.compatible ? styles.version : { ...styles.version, color: FAIR }}>
                                            {server.version ? `v${server.version}` : "–"}
                                        </div>
                                    </div>
                                    <JoinButton blocked={blocked} disabled={busy || blocked} onSelect={() => choose(server)}>
                                        {t(BROWSER_LOC.join, "Join")}
                                    </JoinButton>
                                </div>
                            );
                        })}
                    </div>
                </div>
            </AutoNavigationScope>
            {progress}
        </div>
    );
};
