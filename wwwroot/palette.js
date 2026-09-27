// Bridges the Ctrl+K / Cmd+K shortcut to the Blazor command palette. Kept out of the
// component so the key handler survives navigation between pages.
window.labbyPalette = {
    register(dotNetRef) {
        // A second circuit (a page swap) re-registers; drop the old handler so the
        // shortcut doesn't fire into a disposed component.
        if (this._handler) {
            document.removeEventListener('keydown', this._handler);
        }
        this._handler = event => {
            // event.key can be missing: some autofill and IME events arrive as a keydown
            // with no key at all, and each one would otherwise be an error in the console.
            if ((event.ctrlKey || event.metaKey) && (event.key || '').toLowerCase() === 'k') {
                event.preventDefault();
                dotNetRef.invokeMethodAsync('OpenAsync');
            }
        };
        document.addEventListener('keydown', this._handler);
    },
};
