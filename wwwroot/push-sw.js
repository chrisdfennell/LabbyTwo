// LabbyTwo's service worker. It does one thing: show the notifications the server pushes,
// and open the app when one is tapped. It caches nothing — the dashboard is live data over
// a connection, and an offline copy of it would only ever be a stale one.
//
// Served from the root so its scope is the whole app: a worker only controls pages at or
// below its own path, and a tap should be able to focus whichever LabbyTwo tab is open.

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

self.addEventListener('push', event => {
    let data = {};
    try {
        data = event.data ? event.data.json() : {};
    } catch {
        // Not JSON: show whatever text arrived rather than nothing, because a push that
        // shows no notification is one browsers count against the site.
        data = { body: event.data ? event.data.text() : '' };
    }

    const options = {
        body: data.body || '',
        icon: 'icon-192.png',
        badge: 'icon-192.png',
        data: { url: data.url || '/' },
        timestamp: data.ts || Date.now(),
        // Urgent stays on screen until dealt with; everything else times out as usual.
        requireInteraction: !!data.urgent,
    };

    // One notification per thing: "NAS is back" replaces "NAS is down" rather than piling
    // up beneath it. renotify is only valid with a tag, and makes a replacement buzz again.
    if (data.tag) {
        options.tag = data.tag;
        options.renotify = !!data.renotify;
    }

    event.waitUntil(self.registration.showNotification(data.title || 'LabbyTwo', options));
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const target = new URL((event.notification.data && event.notification.data.url) || '/', self.registration.scope).href;

    event.waitUntil((async () => {
        // An open LabbyTwo is better than a second one: focus it and take it to the page.
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        for (const client of windows) {
            if (new URL(client.url).origin !== self.location.origin)
                continue;
            await client.focus();
            try {
                if (client.url !== target)
                    await client.navigate(target);
            } catch {
                // navigate() is refused for a page this worker does not control yet (opened
                // before it was installed); it is focused, which is most of what was asked.
            }
            return;
        }
        await self.clients.openWindow(target);
    })());
});

// The push service can retire a subscription and issue a new one. Resubscribe with the same
// key and tell the server which row to update, so the device keeps receiving without anybody
// having to press the button again. Chromium rarely fires this; Firefox does.
self.addEventListener('pushsubscriptionchange', event => {
    event.waitUntil((async () => {
        const old = event.oldSubscription;
        const options = old && old.options ? old.options : null;
        if (!options || !options.applicationServerKey)
            return;
        const renewed = event.newSubscription
            || await self.registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: options.applicationServerKey });
        const json = renewed.toJSON();
        await fetch('ext/push/renew', {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ oldEndpoint: old.endpoint, endpoint: json.endpoint, keys: json.keys }),
        });
    })());
});
