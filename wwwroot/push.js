// The browser half of Alerts → Browser push: what this browser can do, and subscribing it.
// Imported as a module by that page only, so no other page pays for it.

let page = null;       // the .NET object to report back to
let serverKey = '';    // this install's VAPID public key, base64url

function isIos() {
    // iPadOS reports itself as a Mac; the touch points give it away.
    return /iPad|iPhone|iPod/.test(navigator.userAgent)
        || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
}

function isStandalone() {
    return window.matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
}

function decode(base64url) {
    const padded = base64url.replace(/-/g, '+').replace(/_/g, '/') + '==='.slice((base64url.length + 3) % 4);
    return Uint8Array.from(atob(padded), c => c.charCodeAt(0));
}

function sameKey(subscription, key) {
    const current = subscription.options && subscription.options.applicationServerKey;
    if (!current)
        return true; // cannot tell; assume it is ours rather than churn the subscription
    const a = new Uint8Array(current), b = decode(key);
    return a.length === b.length && a.every((value, i) => value === b[i]);
}

async function registration() {
    // A registration left by an earlier visit is reused; register() on the same script and
    // scope is a no-op beyond checking for an update.
    return navigator.serviceWorker.register('push-sw.js', { scope: '/' });
}

/** Everything the page needs to explain what this browser can and cannot do. */
export async function status() {
    const result = {
        secure: window.isSecureContext === true,
        serviceWorker: 'serviceWorker' in navigator,
        pushManager: 'PushManager' in window,
        notification: 'Notification' in window,
        permission: 'Notification' in window ? Notification.permission : 'unsupported',
        ios: isIos(),
        standalone: isStandalone(),
        origin: location.origin,
        userAgent: navigator.userAgent,
        endpoint: null,
        workerActive: false,
    };

    if (result.secure && result.serviceWorker) {
        try {
            const reg = await navigator.serviceWorker.getRegistration('/');
            result.workerActive = !!(reg && reg.active);
            if (reg && reg.pushManager) {
                const subscription = await reg.pushManager.getSubscription();
                if (subscription && (!serverKey || sameKey(subscription, serverKey)))
                    result.endpoint = subscription.endpoint;
            }
        } catch {
            // A browser with service workers switched off by policy lands here; it simply
            // has no subscription to report.
        }
    }
    return result;
}

/**
 * Wires the "Notify this device" button. The click is handled here, not through Blazor,
 * because the permission prompt must be asked for inside the tap itself: a click that goes
 * to the server and comes back is no longer a user gesture, and Safari on iOS refuses the
 * prompt outright when it is not one.
 */
export function init(dotnet, publicKey) {
    const first = page === null;
    page = dotnet;
    serverKey = publicKey;
    if (!first)
        return;

    document.addEventListener('click', async event => {
        const button = event.target.closest && event.target.closest('[data-push-subscribe]');
        if (!button || button.disabled || !page)
            return;
        event.preventDefault();

        let permission;
        try {
            permission = await Notification.requestPermission();
        } catch (error) {
            await page.invokeMethodAsync('SubscribeFailed', String(error && error.message || error));
            return;
        }
        if (permission !== 'granted') {
            await page.invokeMethodAsync('SubscribeFailed', permission);
            return;
        }

        try {
            const reg = await registration();
            await navigator.serviceWorker.ready;
            let subscription = await reg.pushManager.getSubscription();

            // Subscribed before, to a key this server no longer has (or another LabbyTwo on
            // the same address): that subscription can never be sent to, so replace it.
            if (subscription && !sameKey(subscription, serverKey)) {
                await subscription.unsubscribe();
                subscription = null;
            }
            if (!subscription) {
                subscription = await reg.pushManager.subscribe({
                    userVisibleOnly: true,
                    applicationServerKey: decode(serverKey),
                });
            }

            const json = subscription.toJSON();
            await page.invokeMethodAsync('Subscribed', json.endpoint, json.keys.p256dh, json.keys.auth, navigator.userAgent);
        } catch (error) {
            await page.invokeMethodAsync('SubscribeFailed', String(error && error.message || error));
        }
    });
}

/** Registers the worker without subscribing, so a page visit alone proves it can be installed. */
export async function prepare() {
    if (!window.isSecureContext || !('serviceWorker' in navigator))
        return false;
    try {
        await registration();
        return true;
    } catch {
        return false;
    }
}

/** Forgets this browser's subscription, for Remove on this device's own row. */
export async function unsubscribe() {
    if (!('serviceWorker' in navigator))
        return false;
    const reg = await navigator.serviceWorker.getRegistration('/');
    const subscription = reg && reg.pushManager ? await reg.pushManager.getSubscription() : null;
    return subscription ? subscription.unsubscribe() : false;
}

export function dispose() {
    page = null;
}
