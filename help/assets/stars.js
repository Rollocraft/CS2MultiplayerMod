(function () {
    "use strict";
    const link = document.getElementById("github-link");
    const count = document.getElementById("github-star-count");
    if (!link || !count) return;
    const key = "docs-stars:" + link.dataset.starsApi;
    function show(value) {
        if (!Number.isSafeInteger(value) || value < 0) return false;
        count.textContent = value.toLocaleString("en-US");
        count.hidden = false;
        link.setAttribute("aria-label", "GitHub, " + count.textContent + " stars");
        return true;
    }
    try {
        const cached = JSON.parse(sessionStorage.getItem(key));
        if (cached && show(cached.count) && Date.now() - cached.time < 3600000) return;
    } catch (_) { /* The counter also works when storage is unavailable. */ }
    fetch(link.dataset.starsApi, {
        headers: { Accept: "application/vnd.github+json" },
        credentials: "omit",
        referrerPolicy: "no-referrer"
    }).then(function (response) {
        if (!response.ok) throw new Error("Repository unavailable");
        return response.json();
    }).then(function (repo) {
        if (!show(repo.stargazers_count)) return;
        try {
            sessionStorage.setItem(key, JSON.stringify({ count: repo.stargazers_count, time: Date.now() }));
        } catch (_) { /* Storage is optional. */ }
    }).catch(function () { /* Keep the GitHub link and any cached count. */ });
})();
