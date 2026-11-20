// Drop-down menus are <details data-menu>: close them on a click elsewhere or on Escape.
document.addEventListener("click", event => {
    for (const menu of document.querySelectorAll("details[data-menu][open]")) {
        if (!menu.contains(event.target)) menu.open = false;
    }
});
document.addEventListener("keydown", event => {
    if (event.key !== "Escape") return;
    for (const menu of document.querySelectorAll("details[data-menu][open]")) {
        menu.open = false;
        menu.querySelector("summary")?.focus();
    }
});
