// Switches push notifications on or off for this browser (page "Benachrichtigungen").
(async () => {
    const status = document.getElementById('push-status');
    const enable = document.getElementById('push-enable');
    const disable = document.getElementById('push-disable');
    const show = text => { status.textContent = text; };

    const ios = /iPhone|iPad/.test(navigator.userAgent);
    const standalone = window.matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
    if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) {
        show(ios && !standalone
            ? 'Auf dem iPhone zuerst die App installieren: Safari → Teilen → „Zum Home-Bildschirm“, dann die App öffnen und hier einschalten.'
            : 'Dieser Browser unterstützt keine Benachrichtigungen (oder die Seite ist nicht mit einem gültigen Zertifikat geöffnet).');
        return;
    }

    const registration = await navigator.serviceWorker.register('/sw.js').then(() => navigator.serviceWorker.ready);
    const current = await registration.pushManager.getSubscription();
    const row = current && [...document.querySelectorAll('tr[data-endpoint]')].find(r => r.dataset.endpoint === current.endpoint);
    if (row) {
        row.querySelector('.this-device').hidden = false;
        show('Auf diesem Gerät eingeschaltet.');
        disable.hidden = false;
    } else if (Notification.permission === 'denied') {
        show('Benachrichtigungen sind für diese Seite im Browser blockiert – in den Website-Einstellungen erlauben und die Seite neu laden.');
    } else {
        show('Auf diesem Gerät ausgeschaltet.');
        enable.hidden = false;
    }

    enable.addEventListener('click', async () => {
        enable.disabled = true;
        try {
            if (await Notification.requestPermission() !== 'granted') {
                show('Nicht erlaubt – Benachrichtigungen bleiben aus.');
                return;
            }
            const key = enable.dataset.key.replace(/-/g, '+').replace(/_/g, '/');
            const raw = Uri8(atob(key + '='.repeat((4 - key.length % 4) % 4)));
            // An existing subscription made for another server key (e.g. after reinstalling) would be refused by the push service.
            let subscription = current;
            if (subscription && !SameKey(subscription.options?.applicationServerKey, raw)) {
                await subscription.unsubscribe().catch(() => { });
                subscription = null;
            }
            subscription ??= await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: raw });
            const json = subscription.toJSON();
            const form = document.getElementById('push-subscribe');
            form.endpoint.value = json.endpoint;
            form.p256dh.value = json.keys.p256dh;
            form.auth.value = json.keys.auth;
            form.submit();
        } catch (error) {
            show('Einschalten fehlgeschlagen: ' + error.message);
        } finally {
            enable.disabled = false;
        }
    });

    disable.addEventListener('click', async () => {
        const form = document.getElementById('push-unsubscribe');
        form.endpoint.value = current.endpoint;
        await current.unsubscribe().catch(() => { });
        form.submit();
    });

    function SameKey(buffer, expected) {
        if (!buffer) return false;
        const actual = new Uint8Array(buffer);
        return actual.length === expected.length && actual.every((b, i) => b === expected[i]);
    }

    function Uri8(text) {
        const bytes = new Uint8Array(text.length);
        for (let i = 0; i < text.length; i++) bytes[i] = text.charCodeAt(i);
        return bytes;
    }
})();
