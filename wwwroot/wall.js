// The browser's half of wall mode (Components/Pages/Wall.razor).
//
// The rotation itself — which tab, when — is decided on the server, where it can be tested.
// What is left here is what only the device can know or do: that somebody touched it, which
// arrow they pressed, what time it is where the screen hangs, whether the mouse has been
// still long enough to hide the pointer, and asking the screen not to go to sleep.
//
// Registered from the page after its first render and torn down when it goes, so none of
// this is left listening on an ordinary dashboard page after somebody leaves the wall.

window.labbyWall = (function () {
    var ref = null;
    var lastReport = 0;
    var cursorTimer = null;
    var clockTimer = null;
    var wakeLock = null;
    var root = document.documentElement;

    // One report a second is enough to hold a pause open; a finger dragging across a
    // tablet fires sixty pointer events in that second, and each would be a round trip.
    var REPORT_EVERY_MS = 1000;
    var CURSOR_IDLE_MS = 3000;

    function report() {
        var now = Date.now();
        if (!ref || now - lastReport < REPORT_EVERY_MS) return;
        lastReport = now;
        ref.invokeMethodAsync('InteractedAsync').catch(function () { });
    }

    function typing(target) {
        return target && (target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName));
    }

    function onKey(event) {
        if (!ref) return;
        // Arrows in a notes box move the caret; they are not a request to change tabs.
        if (typing(event.target) || event.ctrlKey || event.metaKey || event.altKey) {
            report();
            return;
        }
        switch (event.key) {
            case 'ArrowRight':
                event.preventDefault();
                ref.invokeMethodAsync('StepAsync', 1).catch(function () { });
                break;
            case 'ArrowLeft':
                event.preventDefault();
                ref.invokeMethodAsync('StepAsync', -1).catch(function () { });
                break;
            case 'Escape':
                ref.invokeMethodAsync('LeaveAsync').catch(function () { });
                break;
            default:
                report();
        }
    }

    function onPointer(event) {
        // A mouse moving is somebody using the screen, and the pointer has to come back so
        // they can see where it is. A touch has no pointer to show.
        if (event.pointerType === 'mouse') showCursor();
        if (event.type !== 'pointermove' || event.pointerType === 'mouse') report();
    }

    function showCursor() {
        root.classList.remove('wall-cursor-hidden');
        clearTimeout(cursorTimer);
        cursorTimer = setTimeout(function () { root.classList.add('wall-cursor-hidden'); }, CURSOR_IDLE_MS);
    }

    // The clock is the device's, not the server's: a container is often on UTC, and the
    // wall is in somebody's hallway.
    function tick() {
        var now = new Date();
        var text = now.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        var iso = now.toISOString();
        document.querySelectorAll('[data-wall-clock]').forEach(function (el) {
            if (el.textContent !== text) el.textContent = text;
            el.setAttribute('datetime', iso);
        });
    }

    // Screen Wake Lock keeps a tablet from dimming while the wall is up. Only offered in a
    // secure context (https, or localhost), and a lock is dropped by the browser whenever
    // the page is hidden, so it is asked for again each time the page comes back. Where it
    // is missing the wall still works; the device's own sleep setting decides instead, and
    // data-wall-wake on <html> says which happened.
    async function lock() {
        if (!ref) return;
        if (!('wakeLock' in navigator)) {
            root.dataset.wallWake = 'unsupported';
            return;
        }
        if (wakeLock || document.visibilityState !== 'visible') return;
        try {
            wakeLock = await navigator.wakeLock.request('screen');
            root.dataset.wallWake = 'held';
            wakeLock.addEventListener('release', function () {
                wakeLock = null;
                if (ref) root.dataset.wallWake = 'released';
            });
        } catch (e) {
            // Refused: battery saver, a permissions policy, or the page lost focus first.
            root.dataset.wallWake = 'refused';
        }
    }

    function onVisibility() {
        if (document.visibilityState === 'visible') lock();
    }

    return {
        start: function (dotNetRef, slug) {
            this.stop();
            ref = dotNetRef;
            root.classList.add('wall-mode');

            document.addEventListener('keydown', onKey);
            document.addEventListener('pointerdown', onPointer, { passive: true });
            document.addEventListener('pointermove', onPointer, { passive: true });
            document.addEventListener('wheel', report, { passive: true });
            document.addEventListener('visibilitychange', onVisibility);

            showCursor();
            tick();
            clockTimer = setInterval(tick, 1000);
            lock();
            if (slug) this.setAt(slug);
        },

        stop: function () {
            if (!ref) return;
            ref = null;
            document.removeEventListener('keydown', onKey);
            document.removeEventListener('pointerdown', onPointer);
            document.removeEventListener('pointermove', onPointer);
            document.removeEventListener('wheel', report);
            document.removeEventListener('visibilitychange', onVisibility);
            clearTimeout(cursorTimer);
            clearInterval(clockTimer);
            root.classList.remove('wall-mode', 'wall-cursor-hidden');
            delete root.dataset.wallWake;
            if (wakeLock) {
                wakeLock.release().catch(function () { });
                wakeLock = null;
            }
        },

        // Keeps the tab on screen in the address bar without navigating, so a reload lands
        // on it. Blazor's own history state is carried over untouched: it keeps an index in
        // there for back and forward, and a replaceState that dropped it would confuse them.
        setAt: function (slug) {
            var url = new URL(location.href);
            if (url.pathname.replace(/\/$/, '').split('/').pop() !== 'wall') return;
            if (url.searchParams.get('at') === slug) return;
            url.searchParams.set('at', slug);
            history.replaceState(history.state, '', url);
        },
    };
})();
