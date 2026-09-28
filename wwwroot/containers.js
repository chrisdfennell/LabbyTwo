// The Containers tab's few needs from the browser, as a module the page imports when it
// first draws — so nothing here loads for anybody who never opens that tab.

// Tells the page whether it can be seen. Live stats are polled only while it can: a
// dashboard left open in a background tab all day should not be asking a NAS for forty
// containers' stats every ten seconds for nobody.
export function watchVisibility(page) {
    const report = () => page.invokeMethodAsync("SetVisible", document.visibilityState === "visible");
    document.addEventListener("visibilitychange", report);
    report();
    return {
        dispose() {
            document.removeEventListener("visibilitychange", report);
        }
    };
}

// A text file the browser saves, built from what the logs panel already holds.
export function download(name, text) {
    const blob = new Blob([text], { type: "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = name;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

// Keeps a following log pinned to its newest line — unless the reader has scrolled up to
// look at something, in which case yanking them back down would be the worst thing to do.
export function stickToEnd(element) {
    if (!element) return;
    const nearEnd = element.scrollHeight - element.scrollTop - element.clientHeight < 80;
    if (nearEnd || !element.dataset.touched) {
        element.scrollTop = element.scrollHeight;
    }
    if (!element.dataset.watched) {
        element.dataset.watched = "1";
        element.addEventListener("scroll", () => {
            element.dataset.touched = "1";
        }, { passive: true });
    }
}
