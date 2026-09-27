// Drives the reconnect notice in App.razor. Blazor toggles its classes on its own; what
// it leaves to a custom element is deciding what happens once it stops trying. The
// stock dialog waits for someone to click — useless on a wall screen nobody touches —
// so this keeps trying on its own, and reloads when the server no longer knows us.
(function () {
    var notice = document.getElementById('components-reconnect-modal');
    if (!notice) return;

    var timer = null;

    function schedule() {
        clearTimeout(timer);
        timer = setTimeout(retry, 30000);
        document.addEventListener('visibilitychange', retryWhenVisible);
    }

    function retryWhenVisible() {
        if (document.visibilityState === 'visible') retry();
    }

    async function retry() {
        clearTimeout(timer);
        document.removeEventListener('visibilitychange', retryWhenVisible);
        try {
            if (await Blazor.reconnect()) return;
            // The server is back but our circuit is gone: a restart, or a sleep longer
            // than the retention period. A fresh page is the only way forward.
            if (typeof Blazor.resumeCircuit === 'function' && await Blazor.resumeCircuit()) return;
            location.reload();
        } catch {
            // Still unreachable. Try again later rather than give up.
            schedule();
        }
    }

    notice.addEventListener('components-reconnect-state-changed', function (event) {
        switch (event.detail.state) {
            case 'failed': schedule(); break;
            case 'rejected':
            case 'resume-failed': location.reload(); break;
        }
    });
})();
