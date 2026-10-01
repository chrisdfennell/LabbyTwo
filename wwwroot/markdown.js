// The browser's half of a note's code copy buttons (Services/MarkdownCopy.cs), and the
// "is this on screen?" question {{logs: …}} asks before it reads a log again.
//
// One delegated listener rather than a handler per button: the buttons are plain markup
// written by the Markdown renderer, so they work in a note Blazor drew as one piece of
// HTML, in one it rebuilt element by element, and before the circuit has even started.

window.labbyMarkdown = (function () {
    // The clipboard API needs a secure context, and a home lab is very often plain http on
    // a LAN address — so the old copy command is the fallback, and if even that is refused,
    // the code is selected so a long-press or Ctrl+C finishes the job.
    function copyText(text) {
        if (navigator.clipboard && window.isSecureContext) {
            return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return legacyCopy(text); });
        }
        return Promise.resolve(legacyCopy(text));
    }

    function legacyCopy(text) {
        var area = document.createElement('textarea');
        area.value = text;
        area.setAttribute('readonly', '');
        area.style.position = 'fixed';
        area.style.top = '-1000px';
        area.style.opacity = '0';
        document.body.appendChild(area);
        area.select();
        var ok = false;
        try {
            ok = document.execCommand('copy');
        } catch (e) {
            ok = false;
        }
        document.body.removeChild(area);
        return ok;
    }

    function select(element) {
        try {
            var range = document.createRange();
            range.selectNodeContents(element);
            var selection = window.getSelection();
            selection.removeAllRanges();
            selection.addRange(range);
        } catch (e) { }
    }

    function flash(button, word) {
        button.setAttribute('data-state', word);
        button.setAttribute('title', word === 'copied' ? 'Copied' : 'Selected — copy it from here');
        clearTimeout(button._labbyTimer);
        button._labbyTimer = setTimeout(function () {
            button.removeAttribute('data-state');
            button.setAttribute('title', 'Copy');
        }, 1600);
    }

    document.addEventListener('click', function (event) {
        var button = event.target && event.target.closest ? event.target.closest('button.md-copy') : null;
        if (!button) return;
        var holder = button.closest('.md-code, .md-code-inline');
        var code = holder ? holder.querySelector('code') : null;
        if (!code) return;
        event.preventDefault();
        // textContent, not innerText: the code exactly as written, line breaks and all,
        // however the page happens to be wrapping it.
        copyText(code.textContent).then(function (ok) {
            if (ok) {
                flash(button, 'copied');
            } else {
                select(code);
                flash(button, 'selected');
            }
        });
    });

    return {
        // Whether the element is somewhere the reader could be looking at it: the tab is in
        // front, it is not inside a closed fold or a hidden card, and some of it is within
        // the window. Errs towards "yes" when it cannot tell.
        visible: function (element) {
            try {
                if (document.visibilityState === 'hidden') return false;
                if (!element || !element.isConnected) return false;
                if (element.closest('details:not([open])')) return false;
                var box = element.getBoundingClientRect();
                if (box.width === 0 && box.height === 0) return false;
                var height = window.innerHeight || document.documentElement.clientHeight;
                return box.bottom >= -200 && box.top <= height + 200;
            } catch (e) {
                return true;
            }
        }
    };
})();
