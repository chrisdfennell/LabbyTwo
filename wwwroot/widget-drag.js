// Dragging cards around a grid tab.
//
// This used to be HTML5 drag-and-drop wired straight to Blazor, and it was awful for a
// structural reason: `ondragover` fires every few milliseconds while the pointer moves,
// and each one was a SignalR round-trip plus a re-render of the whole grid. The drop
// indicator therefore lagged the pointer by however far away the server is, and on the
// NAS over Wi-Fi that is a visible smear. It also did not exist on a touchscreen, because
// HTML5 drag-and-drop is mouse-only.
//
// So the whole gesture happens here, at frame rate, and the circuit hears exactly once:
// on drop, as a single "this card now goes before that one". Two things make that safe to
// do to a grid Blazor owns:
//
//   * Nothing is moved in the DOM. The preview is done with the CSS `order` property,
//     which re-flows the grid without touching node order, so Blazor's diff still sees
//     the tree it rendered. Moving real nodes under it corrupts the next diff.
//   * The order values we leave behind are 0..n-1 in the order the server is about to
//     persist, so the layout does not flinch between the drop and the re-render. The
//     component clears them in OnAfterRenderAsync, once that re-render has landed.
//
// Registration is delegated from the document rather than bound to the grid element, so
// it survives re-renders and needs nothing re-registered when edit mode toggles.

window.labbyDrag = {
    register(dotNetRef) {
        // A page swap builds a second circuit; drop the old listeners so a disposed
        // component never hears about a drag.
        this.unregister();
        this._ref = dotNetRef;

        this._onDown = e => this._down(e);
        this._onMove = e => this._move(e);
        this._onUp = e => this._up(e, true);
        this._onCancel = e => this._up(e, false);
        this._onKey = e => { if (e.key === 'Escape' && this._drag) this._up(e, false); };

        document.addEventListener('pointerdown', this._onDown, { passive: false });
        document.addEventListener('pointermove', this._onMove, { passive: false });
        document.addEventListener('pointerup', this._onUp);
        document.addEventListener('pointercancel', this._onCancel);
        document.addEventListener('keydown', this._onKey);
    },

    unregister() {
        if (!this._onDown) return;
        document.removeEventListener('pointerdown', this._onDown);
        document.removeEventListener('pointermove', this._onMove);
        document.removeEventListener('pointerup', this._onUp);
        document.removeEventListener('pointercancel', this._onCancel);
        document.removeEventListener('keydown', this._onKey);
        this._onDown = null;
        this._ref = null;
    },

    /// Drops the inline order values once the server's order is on screen.
    clear() {
        document.querySelectorAll('.widget-grid > .widget').forEach(el => {
            el.style.order = '';
            el.style.transition = '';
            el.style.transform = '';
        });
    },

    // ---------- the gesture ----------

    _down(e) {
        if (this._drag || this._pending) return;
        if (e.button !== undefined && e.button !== 0) return;   // left button, or touch/pen

        const card = e.target.closest('.widget-grid > .widget.is-draggable');
        if (!card) return;

        // Anything you could click is not a drag surface, or the toolbar under every card
        // would be unusable.
        if (e.target.closest('button, a, input, select, textarea, [contenteditable]')) return;

        // Touch and pen must use the handle. Pressing anywhere on a card would otherwise
        // eat the scroll gesture, and a dashboard in edit mode is usually taller than the
        // screen — you would be trapped. A mouse has a scroll wheel, so it can grab the
        // whole card, which is what it could always do.
        const viaHandle = !!e.target.closest('.drag-handle');
        if (e.pointerType !== 'mouse' && !viaHandle) return;

        this._pending = {
            card,
            id: card.dataset.widgetId,
            pointerId: e.pointerId,
            startX: e.clientX,
            startY: e.clientY,
            // A handle is unambiguous, so it lifts immediately. Grabbing the body waits
            // for real travel, so a click that wobbles is still a click.
            threshold: viaHandle ? 3 : 6,
        };
    },

    _move(e) {
        if (this._pending && !this._drag) {
            if (e.pointerId !== this._pending.pointerId) return;
            const far = Math.hypot(e.clientX - this._pending.startX, e.clientY - this._pending.startY);
            if (far < this._pending.threshold) return;
            this._start(e);
        }
        if (!this._drag || e.pointerId !== this._drag.pointerId) return;

        e.preventDefault();   // stop touch from scrolling the page out from under the card
        const d = this._drag;
        d.x = e.clientX;
        d.y = e.clientY;
        d.ghost.style.transform =
            `translate(${d.x - d.grabX}px, ${d.y - d.grabY}px) rotate(1.2deg) scale(1.02)`;

        this._autoScroll(e.clientY);
        this._reorderTo(this._targetIndex(e.clientX, e.clientY));
    },

    _start(e) {
        const { card, id, pointerId } = this._pending;
        const grid = card.parentElement;
        const cards = [...grid.querySelectorAll(':scope > .widget')];
        const rect = card.getBoundingClientRect();

        // The lifted card is a clone, so the real one can stay where it is and act as the
        // hole the drop will fill. `pointer-events: none` on it keeps elementFromPoint
        // looking at the grid underneath rather than at the thing following the cursor.
        // Pinned to the viewport origin, with the transform carrying the whole position.
        // Setting left/top to the card's rect as well double-counts it, and the lifted
        // card then sits its own offset away from the pointer — the further down the page
        // it started, the further it flies.
        const ghost = card.cloneNode(true);
        ghost.classList.add('widget-drag-ghost');
        ghost.classList.remove('is-draggable');
        ghost.style.width = `${rect.width}px`;
        ghost.style.height = `${rect.height}px`;
        ghost.style.left = '0';
        ghost.style.top = '0';
        document.body.appendChild(ghost);

        this._drag = {
            card, id, grid, cards, ghost, pointerId,
            grabX: e.clientX - rect.left,
            grabY: e.clientY - rect.top,
            x: e.clientX,
            y: e.clientY,
            order: cards.map(c => c.dataset.widgetId),
            from: cards.indexOf(card),
        };
        this._drag.at = this._drag.from;
        this._pending = null;
        // Placed once up front, or the clone paints at the viewport corner for a frame
        // before the first move moves it.
        ghost.style.transform = `translate(${rect.left}px, ${rect.top}px) rotate(1.2deg) scale(1.02)`;

        card.classList.add('is-dragging');
        document.body.classList.add('is-dragging-widget');
        // Keeps the moves coming if the pointer outruns the card, which it always does.
        try { card.setPointerCapture(pointerId); } catch { /* the element may have gone */ }
    },

    /// Which slot the pointer is asking for. Cards are different widths on a dense grid,
    /// so this goes by the card under the pointer and which half of it you are on, and
    /// falls back to the nearest card when the pointer is in a gap or past the last row.
    _targetIndex(x, y) {
        const d = this._drag;
        let over = document.elementFromPoint(x, y)?.closest('.widget-grid > .widget');
        if (over && !d.cards.includes(over)) over = null;

        if (!over) {
            let best = Infinity;
            for (const c of d.cards) {
                const r = c.getBoundingClientRect();
                const dist = Math.hypot(x - (r.left + r.width / 2), y - (r.top + r.height / 2));
                if (dist < best) { best = dist; over = c; }
            }
        }
        if (!over) return d.at;

        const r = over.getBoundingClientRect();
        const after = x > r.left + r.width / 2;
        // Index within the *current* preview order, which is what the splice below works
        // against — reading it off the original order would fight the last frame.
        const index = d.order.indexOf(over.dataset.widgetId);
        if (index < 0) return d.at;

        const held = d.order.indexOf(d.id);
        let target = after ? index + 1 : index;
        if (held < target) target -= 1;             // removing the held card shifts the rest up
        return Math.max(0, Math.min(d.order.length - 1, target));
    },

    /// Applies a prospective order as CSS `order`, animated FLIP-style: measure, re-flow,
    /// invert, then let the transition play it forward. Without this the cards teleport,
    /// and you cannot see what moved where.
    _reorderTo(index) {
        const d = this._drag;
        if (index === d.at) return;

        const before = new Map(d.cards.map(c => [c, c.getBoundingClientRect()]));

        d.order.splice(d.order.indexOf(d.id), 1);
        d.order.splice(index, 0, d.id);
        d.at = index;
        d.cards.forEach(c => { c.style.order = d.order.indexOf(c.dataset.widgetId); });

        for (const c of d.cards) {
            if (c === d.card) continue;             // the hole itself does not need to slide
            const now = c.getBoundingClientRect();
            const was = before.get(c);
            const dx = was.left - now.left;
            const dy = was.top - now.top;
            if (!dx && !dy) continue;
            c.style.transition = 'none';
            c.style.transform = `translate(${dx}px, ${dy}px)`;
            requestAnimationFrame(() => {
                c.style.transition = 'transform .18s ease';
                c.style.transform = '';
            });
        }
    },

    /// A dashboard is usually taller than the window, so a drag has to be able to reach
    /// the rows it cannot see.
    _autoScroll(y) {
        const margin = 90;
        const speed = 18;
        if (y < margin) window.scrollBy(0, -speed * (1 - y / margin));
        else if (y > window.innerHeight - margin)
            window.scrollBy(0, speed * (1 - (window.innerHeight - y) / margin));
    },

    _up(e, commit) {
        this._pending = null;
        const d = this._drag;
        if (!d) return;
        this._drag = null;

        d.ghost.remove();
        d.card.classList.remove('is-dragging');
        document.body.classList.remove('is-dragging-widget');
        try { d.card.releasePointerCapture(d.pointerId); } catch { /* already gone */ }
        d.cards.forEach(c => { c.style.transition = ''; c.style.transform = ''; });

        if (!commit || d.at === d.from) {
            this.clear();
            return;
        }

        // The server takes "put this one immediately before that one", so the answer is
        // whatever ends up after the held card — or null, meaning the end.
        const before = d.order[d.at + 1] ?? null;
        // A dropped drag on a circuit that has since gone away is not worth an unhandled
        // rejection in the console; the layout on screen is already right either way.
        this._ref?.invokeMethodAsync('DropAsync', d.id, before).catch(() => { });
        // The inline order values stay until the component has re-rendered in the new
        // order; clearing them here would flash the old layout for a frame or two.
    },
};
