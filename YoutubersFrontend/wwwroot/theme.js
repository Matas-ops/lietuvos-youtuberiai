// loaded in <head> so the theme is applied before first paint and the page doesn't flash
(function () {
    let theme = null;

    try {
        theme = localStorage.getItem("theme");
    } catch {
    }

    if (theme !== "light" && theme !== "dark")
        theme = matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";

    document.documentElement.setAttribute("data-bs-theme", theme);

    document.addEventListener("DOMContentLoaded", () => {
        const toggle =
            document.getElementById("themeToggle");

        toggle?.addEventListener("click", () => {
            const root = document.documentElement;

            const next =
                root.getAttribute("data-bs-theme") === "dark" ? "light" : "dark";

            root.setAttribute("data-bs-theme", next);

            try {
                localStorage.setItem("theme", next);
            } catch {
            }
        });
    });
})();
