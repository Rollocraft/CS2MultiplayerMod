/* Full-text search over the build-generated section index. No network requests. */
(function (scope) {
    "use strict";
    function normalize(value) {
        return value.toLowerCase().normalize("NFKD").replace(/[\u0300-\u036f]/g, "");
    }
    function words(query) {
        return Array.from(new Set(normalize(query).match(/[\p{L}\p{N}]+/gu) || [])).slice(0, 16);
    }
    function prepare(documents) {
        return documents.map(function (doc) {
            return { doc: doc, title: normalize(doc.title), heading: normalize(doc.heading), text: normalize(doc.text) };
        });
    }
    function search(index, query, limit) {
        const tokens = words(query);
        if (!tokens.length || normalize(query).trim().length < 2) return [];
        const phrase = normalize(query).trim();
        return index.map(function (entry) {
            let score = 0;
            for (const token of tokens) {
                const inTitle = entry.title.includes(token);
                const inHeading = entry.heading.includes(token);
                const inText = entry.text.includes(token);
                if (!inTitle && !inHeading && !inText) return null;
                score += (inHeading ? 14 : 0) + (inTitle ? 8 : 0) + (inText ? 2 : 0);
            }
            if (entry.heading === phrase) score += 100;
            else if (entry.heading.includes(phrase)) score += 30;
            if (entry.title === phrase) score += 35;
            if (entry.text.includes(phrase)) score += 6;
            return { doc: entry.doc, score: score };
        }).filter(Boolean).sort(function (a, b) {
            return b.score - a.score || a.doc.url.localeCompare(b.doc.url);
        }).slice(0, limit || 12).map(function (hit) { return hit.doc; });
    }
    function snippet(text, query) {
        const lower = normalize(text);
        const matches = words(query).map(function (token) { return lower.indexOf(token); }).filter(function (at) { return at >= 0; });
        let start = matches.length ? Math.max(0, Math.min.apply(null, matches) - 45) : 0;
        if (start > 0) {
            const boundary = text.indexOf(" ", start);
            if (boundary >= 0 && boundary < start + 25) start = boundary + 1;
        }
        return (start ? "…" : "") + text.slice(start, start + 180).trim() + (text.length > start + 180 ? "…" : "");
    }
    if (typeof module !== "undefined" && module.exports) module.exports = { prepare, search, snippet, words };
    if (typeof document === "undefined") return;

    const root = document.getElementById("docs-search");
    const input = document.getElementById("search-input");
    const panel = document.getElementById("search-panel");
    const results = document.getElementById("search-results");
    const status = document.getElementById("search-status");
    if (!root || !input || !panel || !results || !status) return;
    root.hidden = false;
    const available = Array.isArray(scope.DOCS_INDEX);
    const index = prepare(scope.DOCS_INDEX || []);
    const base = new URL(document.body.dataset.root, document.baseURI);
    const hint = root.querySelector("[data-shortcut-modifier]");
    const apple = /Mac|iPhone|iPad|iPod/i.test(navigator.userAgentData?.platform || navigator.platform || navigator.userAgent);
    if (hint) hint.textContent = apple ? "⌘" : "Ctrl";
    input.setAttribute("aria-keyshortcuts", apple ? "Meta+k" : "Control+k");

    function setOpen(open) {
        panel.hidden = !open;
        input.setAttribute("aria-expanded", String(open));
    }
    function highlighted(element, value, query) {
        // Match only literal word tokens; all source text is inserted as text nodes.
        const tokens = words(query).sort(function (a, b) { return b.length - a.length; });
        if (!tokens.length) { element.textContent = value; return; }
        const pattern = new RegExp("(" + tokens.join("|") + ")", "giu");
        let offset = 0;
        for (const match of value.matchAll(pattern)) {
            element.appendChild(document.createTextNode(value.slice(offset, match.index)));
            const mark = document.createElement("mark");
            mark.textContent = match[0];
            element.appendChild(mark);
            offset = match.index + match[0].length;
        }
        element.appendChild(document.createTextNode(value.slice(offset)));
    }
    function refresh() {
        const query = input.value.trim();
        results.replaceChildren();
        if (!available) {
            status.textContent = "Search unavailable. Please reload.";
            setOpen(true);
            return;
        }
        if (query.length < 2 || !words(query).length) {
            status.textContent = "Type to search.";
            setOpen(true);
            return;
        }
        const matches = search(index, query);
        status.textContent = matches.length ? (matches.length === 12 ? "Top 12 matches" : matches.length + (matches.length === 1 ? " match" : " matches")) : "No results.";
        matches.forEach(function (doc) {
            const link = document.createElement("a");
            link.className = "search-result";
            link.href = new URL(doc.url, base).href;
            const group = document.createElement("span");
            group.className = "eyebrow";
            group.textContent = doc.title;
            const title = document.createElement("strong");
            highlighted(title, doc.heading || doc.title, query);
            const summary = document.createElement("p");
            highlighted(summary, snippet(doc.text, query), query);
            link.append(group, title, summary);
            link.addEventListener("click", function () { setOpen(false); });
            results.appendChild(link);
        });
        results.scrollTop = 0;
        setOpen(true);
    }
    input.addEventListener("input", refresh);
    input.addEventListener("focus", refresh);
    root.addEventListener("keydown", function (event) {
        const links = Array.from(results.querySelectorAll("a"));
        if (event.key === "Escape") {
            if (panel.hidden) return;
            event.preventDefault();
            event.stopPropagation();
            input.focus();
            setOpen(false);
        } else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
            event.preventDefault();
            if (panel.hidden) refresh();
            const position = links.indexOf(document.activeElement);
            const next = event.key === "ArrowDown" ? position + 1 : position - 1;
            if (next < 0 || next >= links.length) input.focus();
            else links[next].focus();
        } else if (event.key === "Enter" && event.target === input && !panel.hidden && links.length) {
            event.preventDefault();
            links[0].click();
        }
    });
    root.addEventListener("focusout", function (event) {
        if (!root.contains(event.relatedTarget)) setOpen(false);
    });
    document.addEventListener("click", function (event) { if (!root.contains(event.target)) setOpen(false); });
    document.addEventListener("keydown", function (event) {
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
            event.preventDefault();
            document.dispatchEvent(new CustomEvent("docs:open-search"));
            input.focus();
            input.select();
            refresh();
        }
    });
})(typeof window === "undefined" ? globalThis : window);
