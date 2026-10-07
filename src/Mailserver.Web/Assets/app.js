// Progressive web app: offline page and fast start (service worker), unread count on the app icon.
if ('serviceWorker' in navigator) {
    navigator.serviceWorker.register('/sw.js').catch(() => { /* e.g. untrusted certificate: works as a normal web page */ });
}

const unread = parseInt(document.body.dataset.unread ?? '', 10);
if (!Number.isNaN(unread) && 'setAppBadge' in navigator) {
    (unread > 0 ? navigator.setAppBadge(unread) : navigator.clearAppBadge()).catch(() => { });
}

// Mail list: "Alle" selects every message on the page; the box follows single selections.
document.querySelectorAll('[data-select-all]').forEach(all => {
    const boxes = () => [...all.form.querySelectorAll('input[name="uids"]')];
    all.addEventListener('change', () => boxes().forEach(box => { box.checked = all.checked; }));
    all.form.addEventListener('change', event => {
        if (event.target.name !== 'uids') return;
        const checked = boxes().filter(box => box.checked).length;
        all.checked = checked > 0 && checked === boxes().length;
        all.indeterminate = checked > 0 && !all.checked;
    });
});

// Buttons that destroy data ask first.
document.querySelectorAll('[data-confirm]').forEach(button => button.addEventListener('click', event => {
    if (!confirm(button.dataset.confirm)) event.preventDefault();
}));
