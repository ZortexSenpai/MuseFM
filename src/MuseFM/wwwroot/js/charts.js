// Chart tooltips. Any element with data-tip='{"t": title, "r": [[value, name, seriesKey], ...]}' shows one
// on hover, touch and keyboard focus. Content is set with textContent: names come from the music catalog.
(() => {
    let tip = null;
    let current = null;

    function box() {
        if (!tip) {
            tip = document.createElement("div");
            tip.className = "chart-tip";
            tip.hidden = true;
            document.body.append(tip);
        }
        return tip;
    }

    function render(element) {
        let data;
        try {
            data = JSON.parse(element.dataset.tip);
        } catch {
            return false;
        }
        const target = box();
        const title = document.createElement("div");
        title.className = "chart-tip-title";
        title.textContent = data.t ?? "";
        target.replaceChildren(title);
        for (const [value, name, key] of data.r ?? []) {
            const row = document.createElement("div");
            row.className = "chart-tip-row";
            if (key) {
                const line = document.createElement("i");
                line.className = "key " + key;
                row.append(line);
            }
            const strong = document.createElement("strong");
            strong.textContent = value;
            row.append(strong);
            if (name) {
                const label = document.createElement("span");
                label.textContent = name;
                row.append(label);
            }
            target.append(row);
        }
        return true;
    }

    function show(element, x, y) {
        if (element !== current) {
            if (!render(element)) return;
            current = element;
        }
        const target = box();
        target.hidden = false;
        const gap = 14;
        let left = x + gap;
        let top = y - target.offsetHeight - gap;
        if (left + target.offsetWidth > window.innerWidth - 8) left = x - target.offsetWidth - gap;
        if (top < 8) top = y + gap;
        target.style.left = Math.max(8, left) + "px";
        target.style.top = top + "px";
    }

    function hide() {
        current = null;
        if (tip) tip.hidden = true;
    }

    function fromPointer(event) {
        const element = event.target instanceof Element ? event.target.closest("[data-tip]") : null;
        if (element) show(element, event.clientX, event.clientY);
        else hide();
    }

    document.addEventListener("pointermove", fromPointer, { passive: true });
    document.addEventListener("pointerdown", fromPointer, { passive: true });
    document.addEventListener("pointerout", event => { if (!event.relatedTarget) hide(); });
    document.addEventListener("focusin", event => {
        const element = event.target instanceof Element ? event.target.closest("[data-tip]") : null;
        if (!element) return;
        const rect = element.getBoundingClientRect();
        show(element, rect.left + rect.width / 2, rect.top);
    });
    document.addEventListener("focusout", hide);
    window.addEventListener("scroll", hide, { passive: true });
})();
