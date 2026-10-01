// The browser's half of the "try the phone view" hint (Components/Shared/PhoneHint.razor).
//
// Only the device knows how wide it is and whether somebody already said "not now", so
// those two questions are asked here. The answer to the second is kept in this browser
// alone: dismissing the hint on a phone says nothing about the laptop.

window.labbyPhone = (function () {
    var KEY = 'labbytwo.phoneHint';

    // The phone view is built for 360–430 px; this is a little wider so a large phone in
    // portrait counts, and a tablet does not.
    var NARROW = '(max-width: 600px)';

    return {
        shouldSuggest: function () {
            try {
                if (window.localStorage.getItem(KEY) === 'off') return false;
            } catch (e) {
                // Storage blocked: the hint could never be dismissed, so do not show it at all.
                return false;
            }
            return window.matchMedia(NARROW).matches;
        },
        dismiss: function () {
            try {
                window.localStorage.setItem(KEY, 'off');
            } catch (e) { }
        }
    };
})();
