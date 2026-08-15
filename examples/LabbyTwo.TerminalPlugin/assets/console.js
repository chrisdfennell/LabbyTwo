// The browser half of the terminal. It runs in a document of its own — see the note on
// TerminalEndpoints for why — so it owns the whole page and can be blunt about it.
//
// The wire is deliberately small: binary frames are the terminal stream in both
// directions, and text frames are JSON control. Nothing else is on it.
(function () {
    'use strict';

    var body = document.body;
    var attachPath = body.dataset.attach;
    var where = document.getElementById('where');
    var dot = document.getElementById('dot');
    var again = document.getElementById('again');
    var screen = document.getElementById('screen');

    if (!attachPath || !screen) {
        return;
    }

    var dark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;

    var term = new Terminal({
        cursorBlink: true,
        fontSize: 13,
        fontFamily: 'ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, "Liberation Mono", monospace',
        // Enough to scroll back through a build or a log tail, and not so much that a
        // wall tablet with a forgotten tab holds megabytes of it.
        scrollback: 5000,
        theme: dark
            ? { background: '#16181d', foreground: '#d7dae0', cursor: '#d7dae0', selectionBackground: '#3a3f4b' }
            : { background: '#ffffff', foreground: '#1c1e21', cursor: '#1c1e21', selectionBackground: '#cfe0f5' }
    });

    var fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    term.open(screen);

    var socket = null;
    var encoder = new TextEncoder();

    function size() {
        try {
            fit.fit();
        } catch (e) {
            // The frame can be zero-sized for a tick while the tab lays out. The next
            // observation fixes it, and throwing here would kill the terminal for good.
        }
    }

    function status(state, text) {
        dot.className = state;
        if (text) {
            where.textContent = text;
        }
    }

    function note(text, colour) {
        term.write('\r\n\x1b[' + colour + 'm' + text + '\x1b[0m\r\n');
    }

    function send(data) {
        if (socket && socket.readyState === WebSocket.OPEN) {
            socket.send(data);
        }
    }

    function connect() {
        again.hidden = true;
        status('pending');

        size();

        var url = new URL(attachPath, window.location.href);
        url.protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        url.searchParams.set('cols', String(term.cols));
        url.searchParams.set('rows', String(term.rows));

        socket = new WebSocket(url.href);
        socket.binaryType = 'arraybuffer';

        socket.onopen = function () {
            status('live');
            term.focus();
        };

        socket.onmessage = function (event) {
            if (typeof event.data === 'string') {
                control(event.data);
            } else {
                term.write(new Uint8Array(event.data));
            }
        };

        socket.onclose = function () {
            status('gone');
            again.hidden = false;
        };

        // An error is always followed by a close, so there is nothing to do here that
        // onclose does not already do — saying it twice would just be two red messages.
        socket.onerror = function () { };
    }

    function control(text) {
        var message;
        try {
            message = JSON.parse(text);
        } catch (e) {
            return;
        }

        if (message.t === 'ready') {
            status('live', message.message);
        } else if (message.t === 'error') {
            note(message.message, '31');
        } else if (message.t === 'ended') {
            note(message.message, '2');
        }
    }

    // Keystrokes. onData is text the user typed; onBinary is xterm's own byte-exact
    // channel — mouse reports and the like — where each character is one byte and
    // encoding it as UTF-8 would corrupt anything above 127.
    term.onData(function (data) {
        send(encoder.encode(data));
    });

    term.onBinary(function (data) {
        var bytes = new Uint8Array(data.length);
        for (var i = 0; i < data.length; i++) {
            bytes[i] = data.charCodeAt(i) & 255;
        }
        send(bytes);
    });

    // The far end has to be told, or full-screen programs draw to the wrong shape. This
    // fires from fit(), so it covers the window being resized, the card being made
    // taller, and the phone being turned sideways without any of them being handled.
    term.onResize(function (dimensions) {
        if (socket && socket.readyState === WebSocket.OPEN) {
            socket.send(JSON.stringify({ t: 'resize', cols: dimensions.cols, rows: dimensions.rows }));
        }
    });

    var pending = 0;
    function later() {
        window.clearTimeout(pending);
        pending = window.setTimeout(size, 80);
    }

    if (window.ResizeObserver) {
        new ResizeObserver(later).observe(screen);
    }
    window.addEventListener('resize', later);

    // Clicking anywhere on the page means "I want to type here", including the strip of
    // padding around the emulator that xterm itself does not consider its own.
    document.addEventListener('mousedown', function (event) {
        if (event.target !== again) {
            term.focus();
        }
    });

    again.addEventListener('click', function () {
        term.reset();
        connect();
    });

    connect();
})();
