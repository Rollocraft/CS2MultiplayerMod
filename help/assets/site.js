(function () {
    "use strict";
    const navigation = document.getElementById("guide-navigation");
    const mobile = window.matchMedia("(max-width: 760px)");
    const menu = document.getElementById("mobile-menu");
    const toggle = document.getElementById("menu-toggle");
    const close = document.getElementById("menu-close");
    const search = document.getElementById("docs-search");
    const repository = document.getElementById("github-link");
    function openMenu() {
        if (!mobile.matches || menu.open) return;
        menu.showModal();
        toggle.setAttribute("aria-expanded", "true");
        document.body.classList.add("menu-open");
    }
    function fitNavigation() {
        if (!mobile.matches && menu.open) menu.close();
        const prefix = mobile.matches ? "mobile-" : "desktop-";
        document.getElementById(prefix + "navigation").appendChild(navigation);
        document.getElementById(prefix + "search").appendChild(search);
        document.getElementById(prefix + "repository").appendChild(repository);
    }
    if (navigation && menu && toggle && close) {
        fitNavigation();
        document.documentElement.classList.add("navigation-ready");
        toggle.hidden = false;
        mobile.addEventListener("change", fitNavigation);
        toggle.addEventListener("click", openMenu);
        close.addEventListener("click", function () { menu.close(); });
        menu.addEventListener("close", function () {
            toggle.setAttribute("aria-expanded", "false");
            document.body.classList.remove("menu-open");
        });
        menu.addEventListener("click", function (event) {
            if (event.target.closest("a")) menu.close();
            if (event.target === menu) {
                const rect = menu.getBoundingClientRect();
                if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) menu.close();
            }
        });
        document.addEventListener("docs:open-search", openMenu);
    }

    document.querySelectorAll(".prose img").forEach(function (image) {
        image.loading = "lazy";
        image.decoding = "async";
    });

    // Outline, search and permalink targets may sit inside a folded changelog release.
    function revealTarget(hash) {
        const target = hash && document.getElementById(decodeURIComponent(hash.slice(1)));
        const release = target && target.closest("details.release");
        if (!release || release.open) return;
        release.open = true;
        target.scrollIntoView();
    }
    if (document.querySelectorAll("details.release").length) {
        revealTarget(window.location.hash);
        window.addEventListener("hashchange", function () { revealTarget(window.location.hash); });
        document.addEventListener("click", function (event) {
            const link = event.target.closest("a[href^='#']");
            if (link) revealTarget(link.hash);
        });
    }

    const links = Array.from(document.querySelectorAll(".page-outline nav a"));
    const sections = links.map(function (link) {
        return { link: link, heading: document.getElementById(decodeURIComponent(link.hash.slice(1))) };
    }).filter(function (section) { return section.heading; });
    let scheduled = false;
    function updateOutline() {
        scheduled = false;
        let current = sections[0];
        sections.forEach(function (section) {
            if (section.heading.getBoundingClientRect().top <= 100) current = section;
        });
        sections.forEach(function (section) {
            if (section === current) section.link.setAttribute("aria-current", "location");
            else section.link.removeAttribute("aria-current");
        });
    }
    if (sections.length) {
        updateOutline();
        window.addEventListener("scroll", function () {
            if (!scheduled) { scheduled = true; window.requestAnimationFrame(updateOutline); }
        }, { passive: true });
    }
})();
