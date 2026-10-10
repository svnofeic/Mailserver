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

// Installed app (home screen): there is no browser bar to reload, so pulling down at the top of the page reloads it, as
// in the browser. Pages with unsaved input are left alone, so a half-written mail is not lost.
let edited = false;
document.addEventListener('input', event => {
    if (event.target.closest('form, [contenteditable]')) edited = true;
});
document.addEventListener('submit', () => { edited = false; });

const standalone = window.matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
if (standalone) {
    const threshold = 80;
    const indicator = document.createElement('div');
    indicator.className = 'pull-refresh';
    indicator.setAttribute('aria-hidden', 'true');
    indicator.innerHTML = '<svg viewBox="0 0 24 24"><path d="M21 12a9 9 0 1 1-2.6-6.4L21 8M21 3v5h-5"/></svg>';
    document.body.appendChild(indicator);

    // Inside a scrolled list or text field, pulling down scrolls that element instead.
    const scrolledInside = target => {
        for (let el = target; el && el !== document.body; el = el.parentElement) {
            if (el.scrollTop > 0) return true;
        }
        return false;
    };

    let startY = null;
    let distance = 0;
    const reset = () => {
        startY = null;
        distance = 0;
        indicator.style.transform = '';
        indicator.style.opacity = '';
        indicator.classList.remove('ready');
    };

    document.addEventListener('touchstart', event => {
        if (edited || event.touches.length !== 1 || window.scrollY > 0 || scrolledInside(event.target)) return;
        startY = event.touches[0].clientY;
    }, { passive: true });

    document.addEventListener('touchmove', event => {
        if (startY === null) return;
        distance = event.touches[0].clientY - startY;
        if (distance <= 0 || window.scrollY > 0) {
            indicator.style.opacity = '';
            return;
        }

        const shown = Math.min(distance, threshold * 1.5);
        indicator.style.opacity = String(Math.min(1, shown / threshold));
        indicator.style.transform = `translate(-50%, ${shown * 0.6}px) rotate(${shown * 3}deg)`;
        indicator.classList.toggle('ready', distance >= threshold);
    }, { passive: true });

    document.addEventListener('touchend', () => {
        if (startY !== null && distance >= threshold) {
            indicator.classList.add('loading');
            location.reload();
            return;
        }

        reset();
    });
    document.addEventListener('touchcancel', reset);

    // Back in the app after a while: lists are reloaded, so they do not show an old state.
    let hiddenAt = 0;
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'hidden') {
            hiddenAt = Date.now();
        } else if (hiddenAt && Date.now() - hiddenAt > 60000 && !edited && document.body.dataset.refreshOnResume !== undefined) {
            location.reload();
        }
    });
}
