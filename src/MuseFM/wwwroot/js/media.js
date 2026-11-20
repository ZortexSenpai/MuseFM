// Artwork that is still being looked up arrives as a 2×1 transparent placeholder (a settled "no image" is 1×1).
// Ask again a few times with growing pauses, so covers appear without reloading the page.
(() => {
    const pauses = [2000, 3000, 5000, 8000, 12000, 20000];

    function retry(image) {
        if (!(image instanceof HTMLImageElement) || !image.complete || image.naturalWidth !== 2) return;
        const url = new URL(image.src, location.href);
        // Uploaded profile pictures are never placeholders, even when 2 pixels wide.
        if (!url.pathname.startsWith("/media/") || url.pathname.startsWith("/media/profile/")) return;
        const tries = Number(image.dataset.tries ?? 0);
        if (tries >= pauses.length) return;
        image.dataset.tries = String(tries + 1);
        // A new query string makes the browser request it again instead of reusing the placeholder.
        url.searchParams.set("try", String(tries + 1));
        setTimeout(() => { image.src = url.pathname + url.search; }, pauses[tries]);
    }

    // "load" doesn't bubble, so listen while it travels down to the image.
    document.addEventListener("load", event => retry(event.target), true);
    document.addEventListener("DOMContentLoaded", () => document.querySelectorAll("img").forEach(retry));
})();
