// Progressive web app: offline page and fast start (service worker), unread count on the app icon.
if ('serviceWorker' in navigator) {
    navigator.serviceWorker.register('/sw.js').catch(() => { /* e.g. untrusted certificate: works as a normal web page */ });
}

const unread = parseInt(document.body.dataset.unread ?? '', 10);
if (!Number.isNaN(unread) && 'setAppBadge' in navigator) {
    (unread > 0 ? navigator.setAppBadge(unread) : navigator.clearAppBadge()).catch(() => { });
}
