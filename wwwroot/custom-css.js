// The owner's custom CSS on an open page, and the little code editor it is written in.
//
// The server decides what the CSS is (CustomCssService) and has already checked it; this
// only puts text into <style id="labby-custom">. Safe mode is decided here, per page: a page
// opened with ?safe=1 carries data-safe on <html>, and a push never fills its block — except
// a preview, which somebody asked for in this very tab, so they can try a fix while the
// broken version is kept out of the way.
(function () {
    'use strict';

    var html = document.documentElement;
    var INDENT = '    ';

    function block() { return document.getElementById('labby-custom'); }

    function safe() { return html.hasAttribute('data-safe'); }

    // One number per line, rebuilt only when the count changes — typing within a line
    // touches nothing.
    function number(area, gutter) {
        var count = area.value.split('\n').length;
        if (gutter.dataset.lines === String(count)) return;
        gutter.dataset.lines = String(count);
        var text = '';
        for (var i = 1; i <= count; i++) text += i + '\n';
        gutter.textContent = text;
        gutter.scrollTop = area.scrollTop;
    }

    function insert(area, text) {
        area.focus();
        // execCommand keeps the browser's own undo and fires input like typing does; where it
        // is gone, fall back to editing the value directly.
        if (!document.execCommand || !document.execCommand('insertText', false, text)) {
            area.setRangeText(text, area.selectionStart, area.selectionEnd, 'end');
            area.dispatchEvent(new Event('input', { bubbles: true }));
        }
    }

    window.labbyCustomCss = {
        // A save, from the server. Ends any preview in this tab.
        apply: function (css) {
            var b = block();
            if (!b) return;
            delete b.dataset.preview;
            b.textContent = safe() ? '' : (css || '');
        },

        // This tab only, until the page is left or the CSS is saved or reverted.
        preview: function (css) {
            var b = block();
            if (!b) return;
            b.dataset.preview = '1';
            b.textContent = css || '';
        },

        attach: function (area, gutter) {
            if (!area || area.dataset.attached) return;
            area.dataset.attached = '1';
            var escaped = false;

            area.addEventListener('keydown', function (e) {
                // Tab indents; Escape then Tab leaves the box, so the keyboard is never trapped.
                if (e.key === 'Escape') { escaped = true; return; }
                if (e.key === 'Tab' && !escaped && !e.shiftKey && !e.ctrlKey && !e.altKey && !e.metaKey) {
                    e.preventDefault();
                    insert(area, INDENT);
                }
                escaped = false;
            });

            if (gutter) {
                area.addEventListener('input', function () { number(area, gutter); });
                area.addEventListener('scroll', function () { gutter.scrollTop = area.scrollTop; });
                number(area, gutter);
            }
        },

        setValue: function (area, text) {
            if (!area) return;
            area.value = text || '';
            area.dispatchEvent(new Event('input', { bubbles: true }));
        },

        // Handed to .NET as a stream rather than a string: a stylesheet can be 100 KB, and a
        // message from the browser to a circuit is capped at 32 KB.
        read: function (area) {
            return new TextEncoder().encode(area ? area.value : '');
        }
    };
})();
