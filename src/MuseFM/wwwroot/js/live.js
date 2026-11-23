// Blocks that keep themselves up to date: an element with data-refresh="<url>" replaces its content with the HTML that
// URL returns, every data-refresh-seconds, while the page is visible.
(() => {
    // The browser's own way of writing the markup, so a fetched fragment compares equal to what came with the page.
    function normalize(html) {
        const template = document.createElement("template");
        template.innerHTML = html;
        return template.innerHTML.trim();
    }

    function watch(block) {
        const pause = Math.max(2, Number(block.dataset.refreshSeconds) || 10) * 1000;
        // What the server sent last. The same answer again leaves the block alone, so its animations keep running and
        // its images aren't loaded again.
        let shown = block.innerHTML.trim();
        let timer = 0;
        let busy = false;

        async function refresh() {
            if (busy) return;
            busy = true;
            try {
                // A redirect means the session ended: keep what is there rather than show the login page in the block.
                const response = await fetch(block.dataset.refresh, { cache: "no-store", redirect: "manual" });
                if (!response.ok) return;
                const html = normalize(await response.text());
                if (html === shown) return;
                block.innerHTML = html;
                shown = html;
            } catch {
                // Offline, or the server is restarting: the next round tries again.
            } finally {
                busy = false;
            }
        }

        function schedule() {
            clearInterval(timer);
            timer = document.hidden ? 0 : setInterval(refresh, pause);
        }

        // A tab in the background doesn't ask; it catches up as soon as it is looked at again.
        document.addEventListener("visibilitychange", () => {
            if (!document.hidden) refresh();
            schedule();
        });
        schedule();
    }

    document.querySelectorAll("[data-refresh]").forEach(watch);
})();
