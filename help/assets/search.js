// The header input is the search field; results drop down under it.
(function () {
    var root = document.getElementById("cs2mp-search");
    var input = document.getElementById("cs2mp-search-input");
    var results = document.getElementById("mkdocs-search-results");
    var empty = document.getElementById("cs2mp-search-empty");
    if (!root || !input || !results) return;

    function open(state) {
        root.setAttribute("data-open", state ? "true" : "false");
    }

    function refresh() {
        var hasHits = results.children.length > 0;
        var typed = input.value.trim().length > 2;
        if (empty) empty.style.display = typed && !hasHits ? "block" : "none";
        open(typed);
    }

    // MkDocs indexes the ¶ permalink glyph as page text, so it lands in the
    // result summaries. Strip it as results are rendered.
    new MutationObserver(function () {
        var nodes = results.querySelectorAll("p, h3 a");
        for (var i = 0; i < nodes.length; i++) {
            if (nodes[i].textContent.indexOf("¶") !== -1) {
                nodes[i].textContent = nodes[i].textContent
                    .replace(/¶/g, "")
                    .replace(/\s{2,}/g, " ")
                    .trim();
            }
        }
        refresh();
    }).observe(results, { childList: true });

    input.addEventListener("input", refresh);
    input.addEventListener("focus", refresh);

    input.addEventListener("keydown", function (event) {
        if (event.key === "Escape") {
            input.value = "";
            while (results.firstChild) results.removeChild(results.firstChild);
            open(false);
            input.blur();
        }
        if (event.key === "ArrowDown") {
            var first = results.querySelector("a");
            if (first) {
                event.preventDefault();
                first.focus();
            }
        }
    });

    document.addEventListener("click", function (event) {
        if (!root.contains(event.target)) open(false);
    });

    document.addEventListener("keydown", function (event) {
        if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k") {
            event.preventDefault();
            input.focus();
            input.select();
        }
    });
})();
