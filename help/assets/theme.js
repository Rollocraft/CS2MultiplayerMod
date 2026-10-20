// Apply the saved or system theme before the first stylesheet is painted.
(function () {
    "use strict";
    const system = window.matchMedia("(prefers-color-scheme: dark)");
    let preference = null;
    try { preference = localStorage.getItem("docs-theme"); } catch (_) {}
    function valid(value) { return value === "light" || value === "dark"; }
    function apply() {
        const dark = valid(preference) ? preference === "dark" : system.matches;
        document.documentElement.dataset.theme = dark ? "dark" : "light";
        document.documentElement.classList.toggle("dark", dark);
        const color = document.querySelector('meta[name="theme-color"]');
        if (color) color.content = dark ? "#191a1d" : "#f2f2ef";
        document.querySelectorAll("[data-theme-toggle]").forEach(function (button) {
            const label = dark ? "Switch to light mode" : "Switch to dark mode";
            button.setAttribute("aria-label", label);
            button.title = label;
            button.hidden = false;
        });
    }
    apply();
    system.addEventListener("change", function () { if (!valid(preference)) apply(); });
    window.addEventListener("storage", function (event) {
        if (event.key === "docs-theme" || event.key === null) {
            preference = event.newValue;
            apply();
        }
    });
    document.addEventListener("DOMContentLoaded", function () {
        document.querySelectorAll("[data-theme-toggle]").forEach(function (button) {
            button.addEventListener("click", function () {
                preference = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
                apply();
                try { localStorage.setItem("docs-theme", preference); } catch (_) {}
            });
        });
        apply();
    });
})();
