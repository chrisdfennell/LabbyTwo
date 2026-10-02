// The background and frosted glass: replacing the block on an open page, and sampling a
// picture's colours for the readability estimate on the Appearance page.
//
// Nothing here builds CSS. The block arrives finished from the server (see BackdropCss),
// made only from fixed property names, numbers, parsed colours and the server's own URL.
(function () {
    'use strict';

    function hex(n) {
        var s = n.toString(16);
        return s.length < 2 ? '0' + s : s;
    }

    window.labbyBackdrop = {
        apply: function (css) {
            var block = document.getElementById('labby-backdrop');
            if (block && typeof css === 'string') block.textContent = css;
        },

        // The average colours of a grid over the picture, as #rrggbb strings: the browser
        // scales it down onto a tiny canvas and reads the pixels back. Same origin, so the
        // canvas is not tainted. An empty list if it cannot be loaded or read.
        sample: function (url, size) {
            var n = Math.max(1, Math.min(8, size | 0 || 4));
            return new Promise(function (resolve) {
                var img = new Image();
                img.onload = function () {
                    try {
                        var canvas = document.createElement('canvas');
                        canvas.width = n;
                        canvas.height = n;
                        var g = canvas.getContext('2d');
                        g.imageSmoothingEnabled = true;
                        g.imageSmoothingQuality = 'high';
                        g.drawImage(img, 0, 0, n, n);
                        var data = g.getImageData(0, 0, n, n).data;
                        var out = [];
                        for (var i = 0; i < data.length; i += 4) {
                            out.push('#' + hex(data[i]) + hex(data[i + 1]) + hex(data[i + 2]));
                        }
                        resolve(out);
                    } catch (e) {
                        resolve([]);
                    }
                };
                img.onerror = function () { resolve([]); };
                img.src = url;
            });
        }
    };
})();
