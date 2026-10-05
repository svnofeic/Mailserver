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
