// Restyles an open page when the theme changes, without a reload.
//
// The server sends everything already resolved (see ThemeService.SnapshotAsync): the theme
// block's CSS — built from token names and parsed colours only — the mode for data-theme,
// the accent override and the browser-chrome colour. Nothing here decides anything; it puts
// four values where App.razor would have put them on a fresh load.
(function () {
    'use strict';

    var html = document.documentElement;

    function sync() {
        if (window.labbyThemeSync) window.labbyThemeSync();
    }

    window.labbyTheme = {
        apply: function (snapshot) {
            if (!snapshot) return;

            var block = document.getElementById('labby-theme');
            if (block && typeof snapshot.css === 'string') block.textContent = snapshot.css;

            if (snapshot.mode) html.setAttribute('data-theme', snapshot.mode);
            else html.removeAttribute('data-theme');

            // Only ever a validated #rgb / #rrggbb from the server; anything else is dropped.
            if (snapshot.accent && /^#[0-9a-fA-F]{3}([0-9a-fA-F]{3})?$/.test(snapshot.accent)) {
                html.style.setProperty('--accent', snapshot.accent);
            } else {
                html.style.removeProperty('--accent');
            }

            var meta = document.querySelector('meta[name="theme-color"]');
            if (meta && snapshot.meta) meta.setAttribute('content', snapshot.meta);

            sync();
        }
    };

    // Enhanced navigation patches the document from the server's copy, which can put the
    // pre-script data-bs-theme back. Re-derive it after every one.
    if (window.Blazor && window.Blazor.addEventListener) {
        window.Blazor.addEventListener('enhancedload', sync);
    } else {
        document.addEventListener('DOMContentLoaded', function () {
            if (window.Blazor && window.Blazor.addEventListener) window.Blazor.addEventListener('enhancedload', sync);
        });
    }
})();
