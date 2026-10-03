// Which sidebar groups this browser has folded away. Per browser on purpose: how somebody
// likes their sidebar is not a setting of the lab. Storage can be missing or refuse (a private
// window, blocked site data), and then every group is simply open.
window.labbyNav = {
    key: 'labby.nav.folded',

    folded() {
        try {
            const stored = JSON.parse(localStorage.getItem(this.key) || '[]');
            return Array.isArray(stored) ? stored.filter(k => typeof k === 'string') : [];
        } catch {
            return [];
        }
    },

    fold(keys) {
        try {
            localStorage.setItem(this.key, JSON.stringify(keys || []));
        } catch {
            // Nowhere to keep it; the fold lasts for this visit.
        }
    },
};
