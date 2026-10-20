export interface ChatEntry {
    id: number;
    kind: "message" | "activity" | "error";
    isLocal: boolean;
    sender: string | null;
    text: string;
    time: string;
    timestamp?: number;
}

export interface ChatGroup {
    entries: ChatEntry[];
}

const GROUP_GAP_MS = 2 * 60 * 1000;

const nearby = (previous: ChatEntry, current: ChatEntry): boolean => {
    if (previous.timestamp !== undefined && current.timestamp !== undefined) {
        const gap = current.timestamp - previous.timestamp;
        return Number.isFinite(gap) && gap >= 0 && gap <= GROUP_GAP_MS;
    }
    // A running older assembly provides HH:mm until the next restart.
    const minutes = (time: string) => {
        const match = /^(\d{2}):(\d{2})$/.exec(time);
        return match ? Number(match[1]) * 60 + Number(match[2]) : NaN;
    };
    const gap = (minutes(current.time) - minutes(previous.time) + 1440) % 1440;
    return gap >= 0 && gap <= GROUP_GAP_MS / 60000;
};

export const groupChatMessages = (entries: ChatEntry[]): ChatGroup[] => {
    const groups: ChatGroup[] = [];
    for (const entry of entries) {
        if (entry.kind !== "message") continue;
        const group = groups[groups.length - 1];
        const previous = group?.entries[group.entries.length - 1];
        if (previous && previous.sender === entry.sender && previous.isLocal === entry.isLocal && nearby(previous, entry))
            group.entries.push(entry);
        else
            groups.push({ entries: [entry] });
    }
    return groups;
};
