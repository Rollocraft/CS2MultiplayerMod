// Search, status and tester filters plus sorting for the mod compatibility
// table. The table stays plain Markdown; without this script it still reads.
(function () {
    var host = document.querySelector(".mod-table");
    var table = host && host.querySelector("table");
    if (!table || !table.tHead || !table.tBodies.length) return;

    // Status comes from the emoji the row starts with.
    var STATUSES = [
        { key: "official", mark: "🛠", label: "Official" },
        { key: "works", mark: "🟢", label: "Works" },
        { key: "partial", mark: "🟡", label: "Partially works" },
        { key: "broken", mark: "🔴", label: "Doesn't work" }
    ];
    var COL = { status: 0, mod: 1, details: 2, testers: 3 };

    var body = table.tBodies[0];
    var headers = [].map.call(table.tHead.rows[0].cells, function (th) {
        return th.textContent.trim();
    });
    var state = { q: "", status: "", tester: "", sortKey: "", sortDir: 1 };

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) node.className = className;
        if (text != null) node.textContent = text;
        return node;
    }

    function statusOf(text) {
        for (var i = 0; i < STATUSES.length; i++) {
            if (text.indexOf(STATUSES[i].mark) !== -1) return i;
        }
        return -1;
    }

    function statusPill(index, text) {
        var pill = el("span", "mod-status", text);
        if (index >= 0) pill.setAttribute("data-status", STATUSES[index].key);
        return pill;
    }

    var testerCounts = {};
    var rows = [].map.call(body.rows, function (tr, order) {
        var cells = tr.cells;
        var statusText = cells[COL.status].textContent.replace(/^[^A-Za-z]+/, "").trim();
        var status = statusOf(cells[COL.status].textContent);
        cells[COL.status].replaceChildren(statusPill(status, statusText));

        var testers = cells[COL.testers].textContent.split(",")
            .map(function (s) { return s.trim(); })
            .filter(Boolean);
        var list = el("span", "mod-testers");
        testers.forEach(function (name) {
            testerCounts[name] = (testerCounts[name] || 0) + 1;
            var chip = el("button", "mod-tester", name);
            chip.type = "button";
            chip.setAttribute("data-tester", name);
            chip.title = "Show only mods tested by " + name;
            list.appendChild(chip);
        });
        cells[COL.testers].replaceChildren(list);

        [].forEach.call(cells, function (td, i) {
            td.setAttribute("data-label", headers[i] || "");
        });

        return {
            tr: tr,
            order: order,
            status: status,
            statusKey: status >= 0 ? STATUSES[status].key : "",
            testers: testers,
            name: cells[COL.mod].textContent.trim(),
            text: tr.textContent.toLowerCase()
        };
    });

    // ---------------------------------------------------------------- toolbar

    var bar = el("div", "mod-filter");

    var search = el("label", "mod-filter__search");
    search.innerHTML =
        '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" ' +
        'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
        '<circle cx="11" cy="11" r="8"></circle><path d="m21 21-4.3-4.3"></path></svg>';
    var input = el("input");
    input.type = "search";
    input.placeholder = "Search mods, details or testers";
    input.setAttribute("aria-label", "Search mods");
    search.appendChild(input);

    var group = el("div", "mod-filter__status");
    group.setAttribute("role", "group");
    group.setAttribute("aria-label", "Status");
    var chips = [{ key: "", label: "All" }].concat(STATUSES).map(function (s) {
        var chip = el("button", "mod-chip");
        chip.type = "button";
        chip.setAttribute("data-status", s.key);
        if (s.key) chip.appendChild(el("span", "mod-chip__dot"));
        chip.appendChild(document.createTextNode(s.label));
        var count = el("span", "mod-chip__count");
        chip.appendChild(count);
        chip.addEventListener("click", function () {
            state.status = state.status === s.key ? "" : s.key;
            update();
        });
        group.appendChild(chip);
        return { key: s.key, node: chip, count: count };
    });

    var select = el("select", "mod-filter__tester");
    select.setAttribute("aria-label", "Tested by");
    select.appendChild(new Option("All testers", ""));
    Object.keys(testerCounts)
        .sort(function (a, b) { return a.localeCompare(b); })
        .forEach(function (name) {
            select.appendChild(new Option(name + " (" + testerCounts[name] + ")", name));
        });

    bar.appendChild(search);
    bar.appendChild(select);
    bar.appendChild(group);

    var summary = el("p", "mod-filter__summary");
    summary.setAttribute("aria-live", "polite");
    var summaryText = el("span");
    var reset = el("button", "mod-filter__reset", "Reset filters");
    reset.type = "button";
    summary.appendChild(summaryText);
    summary.appendChild(reset);

    var anchor = table.closest(".table-wrapper") || table;
    anchor.parentNode.insertBefore(bar, anchor);
    anchor.parentNode.insertBefore(summary, anchor);

    var emptyRow = el("tr", "mod-table__empty");
    var emptyCell = el("td", null, "No mods match these filters.");
    emptyCell.colSpan = headers.length;
    emptyRow.appendChild(emptyCell);

    // ---------------------------------------------------------------- sorting

    var sortable = [
        { col: COL.status, key: "status" },
        { col: COL.mod, key: "mod" }
    ].map(function (s) {
        var th = table.tHead.rows[0].cells[s.col];
        var button = el("button", "mod-sort", th.textContent.trim());
        button.type = "button";
        button.addEventListener("click", function () {
            if (state.sortKey !== s.key) {
                state.sortKey = s.key;
                state.sortDir = 1;
            } else if (state.sortDir === 1) {
                state.sortDir = -1;
            } else {
                state.sortKey = "";
            }
            update();
        });
        th.replaceChildren(button);
        return { th: th, key: s.key };
    });

    function compare(a, b) {
        var result = 0;
        if (state.sortKey === "status") result = a.status - b.status;
        if (state.sortKey === "mod") result = a.name.localeCompare(b.name);
        return result * state.sortDir || a.order - b.order;
    }

    // ------------------------------------------------------------- filtering

    function matches(row, ignoreStatus) {
        if (!ignoreStatus && state.status && row.statusKey !== state.status) return false;
        if (state.tester && row.testers.indexOf(state.tester) === -1) return false;
        if (state.q && row.text.indexOf(state.q) === -1) return false;
        return true;
    }

    function update() {
        var shown = 0;
        rows.slice().sort(compare).forEach(function (row) {
            row.tr.hidden = !matches(row, false);
            if (!row.tr.hidden) shown++;
            body.appendChild(row.tr);
        });

        if (shown === 0) body.appendChild(emptyRow);
        else if (emptyRow.parentNode) emptyRow.parentNode.removeChild(emptyRow);

        chips.forEach(function (chip) {
            var n = rows.filter(function (row) {
                return matches(row, true) && (!chip.key || row.statusKey === chip.key);
            }).length;
            chip.count.textContent = n;
            chip.node.setAttribute("aria-pressed", String(state.status === chip.key));
        });

        sortable.forEach(function (s) {
            var dir = state.sortKey === s.key ? (state.sortDir === 1 ? "ascending" : "descending") : "none";
            s.th.setAttribute("aria-sort", dir);
        });

        body.querySelectorAll(".mod-tester").forEach(function (chip) {
            chip.setAttribute("data-active", String(chip.getAttribute("data-tester") === state.tester));
        });

        select.value = state.tester;
        var filtered = state.q || state.status || state.tester;
        summaryText.textContent = filtered
            ? "Showing " + shown + " of " + rows.length + " mods"
            : rows.length + " mods";
        reset.hidden = !filtered;

        writeUrl();
    }

    // ------------------------------------------------------ shareable state

    function readUrl() {
        var params = new URLSearchParams(location.search);
        var status = params.get("status") || "";
        state.status = STATUSES.some(function (s) { return s.key === status; }) ? status : "";
        state.tester = testerCounts[params.get("tester")] ? params.get("tester") : "";
        input.value = params.get("q") || "";
        state.q = input.value.trim().toLowerCase();
    }

    function writeUrl() {
        var params = new URLSearchParams(location.search);
        [["q", input.value.trim()], ["status", state.status], ["tester", state.tester]].forEach(function (p) {
            if (p[1]) params.set(p[0], p[1]);
            else params.delete(p[0]);
        });
        var query = params.toString();
        history.replaceState(history.state, "", location.pathname + (query ? "?" + query : "") + location.hash);
    }

    // ---------------------------------------------------------------- events

    input.addEventListener("input", function () {
        state.q = input.value.trim().toLowerCase();
        update();
    });

    input.addEventListener("keydown", function (event) {
        if (event.key === "Escape" && input.value) {
            event.stopPropagation();
            input.value = "";
            state.q = "";
            update();
        }
    });

    select.addEventListener("change", function () {
        state.tester = select.value;
        update();
    });

    body.addEventListener("click", function (event) {
        var chip = event.target.closest(".mod-tester");
        if (!chip) return;
        var name = chip.getAttribute("data-tester");
        state.tester = state.tester === name ? "" : name;
        update();
    });

    reset.addEventListener("click", function () {
        input.value = "";
        state.q = state.status = state.tester = "";
        update();
    });

    readUrl();
    host.classList.add("is-ready");
    update();
})();
