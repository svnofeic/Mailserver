// Service worker of the mail server web interface. Only the design files and the offline page are cached – never
// mails or other personal data, so nothing stays on a shared device after logging out.
const VERSION = '__VERSION__';
const CACHE = 'mailserver-' + VERSION;
const ASSETS = ['/offline', '/assets/site.css?v=' + VERSION, '/assets/app.js?v=' + VERSION, '/assets/icons/icon-192.png'];

self.addEventListener('install', event => {
    event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(ASSETS)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', event => {
    event.waitUntil(caches.keys()
        .then(keys => Promise.all(keys.filter(key => key.startsWith('mailserver-') && key !== CACHE).map(key => caches.delete(key))))
        .then(() => self.clients.claim()));
});

// Push notification for new mail. The browser has already decrypted the message.
self.addEventListener('push', event => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; } catch { data = { title: 'Neue Mail', body: event.data?.text() }; }
    const work = [self.registration.showNotification(data.title || 'Neue Mail', {
        body: data.body || '',
        icon: '/assets/icons/icon-192.png',
        badge: '/assets/icons/icon-192.png',
        tag: data.tag || 'inbox',
        renotify: true,
        data: { url: data.url || '/Mail' },
    })];
    if (typeof data.unread === 'number' && 'setAppBadge' in self.navigator) {
        work.push((data.unread > 0 ? self.navigator.setAppBadge(data.unread) : self.navigator.clearAppBadge()).catch(() => { }));
    }
    event.waitUntil(Promise.all(work));
});

// Tapping the notification opens the mail – in an open window of the app if there is one.
self.addEventListener('notificationclick', event => {
    event.notification.close();
    const url = new URL(event.notification.data?.url || '/Mail', self.location.origin).href;
    event.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(windows => {
        const open = windows.find(w => new URL(w.url).origin === self.location.origin);
        return open ? open.navigate(url).then(w => (w || open).focus()) : self.clients.openWindow(url);
    }));
});

self.addEventListener('fetch', event => {
    const request = event.request;
    const url = new URL(request.url);
    if (request.method !== 'GET' || url.origin !== self.location.origin) {
        return;
    }

    if (request.mode === 'navigate') {
        // Pages always come from the server; without a connection the offline page is shown.
        event.respondWith(fetch(request).catch(() => caches.match('/offline')));
        return;
    }

    if (url.pathname.startsWith('/assets/')) {
        event.respondWith(caches.match(request).then(hit => hit || fetch(request).then(response => {
            if (response.ok) {
                const copy = response.clone();
                caches.open(CACHE).then(cache => cache.put(request, copy));
            }
            return response;
        })));
    }
});
