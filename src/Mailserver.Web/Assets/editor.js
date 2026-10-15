// Rich text editor for webmail. Plain JavaScript without dependencies, served by the mail server itself.
// Without JavaScript the form keeps working with the plain text field.
(function () {
    'use strict';

    var form = document.querySelector('form.compose');
    if (!form) { return; }
    var textarea = form.querySelector('textarea[name="Form.Body"]');
    var htmlField = form.querySelector('input[name="Form.BodyHtml"]');
    if (!textarea || !htmlField || typeof document.execCommand !== 'function') { return; }

    function escapeHtml(text) {
        return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    var editor = document.createElement('div');
    editor.className = 'editor-area';
    editor.contentEditable = 'true';
    editor.setAttribute('role', 'textbox');
    editor.setAttribute('aria-multiline', 'true');
    editor.setAttribute('aria-label', 'Nachricht');
    // The server sanitized this HTML; the page's Content-Security-Policy blocks scripts in it as well.
    editor.innerHTML = htmlField.value ? htmlField.value : escapeHtml(textarea.value).replace(/\n/g, '<br>');

    function run(command, value) {
        editor.focus();
        document.execCommand(command, false, value);
    }

    var toolbar = document.createElement('div');
    toolbar.className = 'editor-toolbar';
    toolbar.setAttribute('role', 'toolbar');
    toolbar.setAttribute('aria-label', 'Formatierung');

    function button(label, title, action, className) {
        var b = document.createElement('button');
        b.type = 'button';
        b.textContent = label;
        b.title = title;
        b.setAttribute('aria-label', title);
        if (className) { b.className = className; }
        // Keep the text selection when clicking the toolbar.
        b.addEventListener('mousedown', function (e) { e.preventDefault(); });
        b.addEventListener('click', function (e) { e.preventDefault(); action(); });
        toolbar.appendChild(b);
    }

    function separator() {
        var s = document.createElement('span');
        s.className = 'sep';
        toolbar.appendChild(s);
    }

    document.execCommand('styleWithCSS', false, false);
    button('F', 'Fett (Strg+B)', function () { run('bold'); }, 'b');
    button('K', 'Kursiv (Strg+I)', function () { run('italic'); }, 'i');
    button('U', 'Unterstrichen (Strg+U)', function () { run('underline'); }, 'u');
    button('S', 'Durchgestrichen', function () { run('strikeThrough'); }, 's');
    separator();
    button('Überschrift', 'Überschrift', function () { run('formatBlock', '<h3>'); });
    button('Absatz', 'Normaler Text', function () { run('formatBlock', '<p>'); });
    button('• Liste', 'Aufzählung', function () { run('insertUnorderedList'); });
    button('1. Liste', 'Nummerierte Liste', function () { run('insertOrderedList'); });
    button('❝ Zitat', 'Zitat', function () { run('formatBlock', '<blockquote>'); });
    button('⇤', 'Einzug verkleinern', function () { run('outdent'); });
    button('⇥', 'Einzug vergrößern', function () { run('indent'); });
    separator();
    button('Link', 'Link einfügen', function () {
        var url = window.prompt('Adresse des Links (https://…  oder  mailto:…)', 'https://');
        if (url && /^(https?:\/\/|mailto:)/i.test(url.trim())) { run('createLink', url.trim()); }
    });
    button('Link entfernen', 'Link entfernen', function () { run('unlink'); });
    button('🖼 Bild', 'Bild in den Text einfügen', function () { saveSelection(); picturePicker.click(); });

    var colors = document.createElement('select');
    colors.title = 'Textfarbe';
    colors.setAttribute('aria-label', 'Textfarbe');
    [['', 'Farbe'], ['#1d2330', 'Schwarz'], ['#b42318', 'Rot'], ['#2457c5', 'Blau'], ['#1d7a46', 'Grün'], ['#c2410c', 'Orange'], ['#6b7280', 'Grau']]
        .forEach(function (c) {
            var o = document.createElement('option');
            o.value = c[0];
            o.textContent = c[1];
            colors.appendChild(o);
        });
    colors.addEventListener('change', function () {
        if (!colors.value) { return; }
        document.execCommand('styleWithCSS', false, true);
        run('foreColor', colors.value);
        document.execCommand('styleWithCSS', false, false);
        colors.value = '';
    });
    toolbar.appendChild(colors);

    separator();
    button('Formatierung entfernen', 'Formatierung entfernen', function () { run('removeFormat'); run('formatBlock', '<p>'); });
    button('↶', 'Rückgängig (Strg+Z)', function () { run('undo'); });
    button('↷', 'Wiederholen (Strg+Y)', function () { run('redo'); });

    // Pictures in the text: chosen with the button, pasted or dragged in. Large photos are scaled down (longest side
    // 1600 px) so the mail stays small; the server turns them into embedded parts when sending.
    var maxSide = 1600;
    var maxBytes = 5 * 1024 * 1024;
    var savedRange = null;
    function saveSelection() {
        var selection = window.getSelection();
        if (selection.rangeCount > 0 && editor.contains(selection.getRangeAt(0).commonAncestorContainer)) {
            savedRange = selection.getRangeAt(0).cloneRange();
        }
    }
    function restoreSelection() {
        editor.focus();
        if (savedRange) {
            var selection = window.getSelection();
            selection.removeAllRanges();
            selection.addRange(savedRange);
            savedRange = null;
        }
    }
    function insertPicture(url) {
        restoreSelection();
        document.execCommand('insertHTML', false, '<img src="' + url + '" alt="" style="max-width: 100%; height: auto;">');
    }
    function readPicture(file) {
        if (!/^image\/(png|jpeg|gif|webp)$/i.test(file.type)) {
            window.alert('„' + file.name + '“ ist kein Bild im Format JPEG, PNG, GIF oder WebP. Andere Dateien bitte als Anhang hinzufügen.');
            return;
        }
        var reader = new FileReader();
        reader.onload = function () {
            var url = reader.result;
            // GIFs keep their animation; everything else is scaled down if it is big.
            if (file.type === 'image/gif') { finish(url); return; }
            var image = new Image();
            image.onload = function () {
                var scale = Math.min(1, maxSide / Math.max(image.width, image.height));
                if (scale === 1 && file.size <= 600 * 1024) { finish(url); return; }
                var canvas = document.createElement('canvas');
                canvas.width = Math.round(image.width * scale);
                canvas.height = Math.round(image.height * scale);
                canvas.getContext('2d').drawImage(image, 0, 0, canvas.width, canvas.height);
                // PNG stays PNG (screenshots, transparency) if that is not much bigger; photos become JPEG.
                var png = file.type === 'image/png' ? canvas.toDataURL('image/png') : null;
                var jpeg = canvas.toDataURL('image/jpeg', 0.85);
                finish(png && png.length < jpeg.length * 1.5 ? png : jpeg);
            };
            image.onerror = function () { finish(url); };
            image.src = url;
        };
        function finish(url) {
            if (url.length * 0.75 > maxBytes) {
                window.alert('„' + file.name + '“ ist zu groß für den Text (höchstens 5 MB). Bitte als Anhang hinzufügen.');
                return;
            }
            insertPicture(url);
        }
        reader.readAsDataURL(file);
    }
    var picturePicker = document.createElement('input');
    picturePicker.type = 'file';
    picturePicker.accept = 'image/png,image/jpeg,image/gif,image/webp';
    picturePicker.multiple = true;
    picturePicker.hidden = true;
    picturePicker.addEventListener('change', function () {
        Array.prototype.forEach.call(picturePicker.files, readPicture);
        picturePicker.value = '';
    });
    form.appendChild(picturePicker);
    editor.addEventListener('paste', function (event) {
        var items = event.clipboardData ? Array.prototype.slice.call(event.clipboardData.items || []) : [];
        var pictures = items.filter(function (item) { return item.kind === 'file' && /^image\//.test(item.type); });
        if (pictures.length > 0) {
            event.preventDefault();
            saveSelection();
            pictures.forEach(function (item) { var file = item.getAsFile(); if (file) { readPicture(file); } });
        }
    });
    editor.addEventListener('dragover', function (event) {
        if (event.dataTransfer && Array.prototype.indexOf.call(event.dataTransfer.types, 'Files') >= 0) {
            event.preventDefault();
            event.stopPropagation();
        }
    });
    editor.addEventListener('drop', function (event) {
        var files = event.dataTransfer ? Array.prototype.slice.call(event.dataTransfer.files || []) : [];
        var pictures = files.filter(function (file) { return /^image\/(png|jpeg|gif|webp)$/i.test(file.type); });
        if (pictures.length === 0) { return; }
        // Pictures dropped onto the text go into the text; other files (and drops beside it) become attachments.
        event.preventDefault();
        event.stopPropagation();
        if (document.caretRangeFromPoint) {
            var range = document.caretRangeFromPoint(event.clientX, event.clientY);
            if (range) { savedRange = range; }
        }
        pictures.forEach(readPicture);
    });

    textarea.style.display = 'none';
    textarea.parentNode.insertBefore(toolbar, textarea);
    textarea.parentNode.insertBefore(editor, textarea);

    // Choosing another sender swaps the signature (only the one the server put in, not one inside quoted text).
    var from = form.querySelector('select[name="Form.From"]');
    var signatures = {};
    try { signatures = JSON.parse((from && from.getAttribute('data-signatures')) || '{}'); } catch (e) { signatures = {}; }
    if (from) {
        from.addEventListener('change', function () {
            var current = editor.querySelector(':scope > .signature');
            var html = signatures[from.value.toLowerCase()];
            var replacement = null;
            if (html) {
                var holder = document.createElement('div');
                holder.innerHTML = html;
                replacement = holder.firstChild;
            }
            if (current && replacement) { current.replaceWith(replacement); }
            else if (current) { current.remove(); }
            else if (replacement) {
                // Below the first line (where the cursor starts), above any quoted text.
                var first = editor.firstChild;
                editor.insertBefore(replacement, first ? first.nextSibling : null);
            }
        });
    }
    form.addEventListener('submit', function () {
        htmlField.value = editor.innerHTML;
        textarea.value = editor.innerText;
    });
})();

// Attachments: each choice (or files dragged onto the form) adds to the list instead of replacing it, and every file can
// be removed again. The list is handed to the normal file field before sending, so the server sees a plain upload.
(function () {
    'use strict';

    var zone = document.querySelector('[data-attachments]');
    var input = zone && zone.querySelector('input[type="file"][name="Files"]');
    var list = zone && zone.querySelector('[data-attachment-list]');
    if (!input || !list || typeof DataTransfer === 'undefined') { return; }
    try { new DataTransfer(); } catch (e) { return; }

    var max = parseInt(zone.dataset.max || '0', 10);
    var files = [];

    function size(bytes) {
        if (bytes < 1024) { return bytes + ' B'; }
        if (bytes < 1024 * 1024) { return Math.round(bytes / 1024) + ' KB'; }
        return (bytes / 1024 / 1024).toFixed(1).replace('.', ',') + ' MB';
    }

    function sync() {
        var transfer = new DataTransfer();
        files.forEach(function (file) { transfer.items.add(file); });
        input.files = transfer.files;
        render();
    }

    function render() {
        list.replaceChildren();
        files.forEach(function (file, index) {
            var item = document.createElement('li');
            var name = document.createElement('span');
            name.textContent = '📎 ' + file.name;
            var info = document.createElement('span');
            info.className = 'small muted';
            info.textContent = size(file.size);
            var remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'link remove';
            remove.textContent = 'Entfernen';
            remove.setAttribute('aria-label', file.name + ' entfernen');
            remove.addEventListener('click', function () {
                files.splice(index, 1);
                sync();
            });
            item.append(name, info, remove);
            list.appendChild(item);
        });

        var total = files.reduce(function (sum, file) { return sum + file.size; }, 0);
        if (max > 0 && total > max) {
            var warning = document.createElement('li');
            warning.className = 'too-large';
            warning.textContent = 'Zusammen ' + size(total) + ' – mehr als die erlaubten ' + size(max) + '. Bitte Dateien entfernen.';
            list.appendChild(warning);
        }
    }

    function add(selected) {
        Array.prototype.forEach.call(selected, function (file) {
            var known = files.some(function (f) { return f.name === file.name && f.size === file.size && f.lastModified === file.lastModified; });
            if (!known) { files.push(file); }
        });
        sync();
    }

    // A separate picker: choosing again adds files; the real field only carries the list.
    var picker = document.createElement('input');
    picker.type = 'file';
    picker.multiple = true;
    picker.hidden = true;
    picker.addEventListener('change', function () {
        add(picker.files);
        picker.value = '';
    });
    var button = document.createElement('button');
    button.type = 'button';
    button.className = 'secondary';
    button.textContent = '📎 Datei hinzufügen';
    button.addEventListener('click', function () { picker.click(); });
    var label = input.closest('label');
    (label || input).hidden = true;
    zone.append(picker, button);

    var form = zone.closest('form');
    form.addEventListener('dragover', function (event) {
        if (event.dataTransfer && Array.prototype.indexOf.call(event.dataTransfer.types, 'Files') >= 0) {
            event.preventDefault();
            zone.classList.add('dropping');
        }
    });
    form.addEventListener('dragleave', function (event) {
        if (!form.contains(event.relatedTarget)) { zone.classList.remove('dropping'); }
    });
    form.addEventListener('drop', function (event) {
        if (event.dataTransfer && event.dataTransfer.files.length > 0) {
            event.preventDefault();
            zone.classList.remove('dropping');
            add(event.dataTransfer.files);
        }
    });
})();
// Recipient suggestions from the address book while typing in To, Cc and Bcc (the part after the last comma).
(function () {
    'use strict';
    var fields = document.querySelectorAll('form.compose input[name="Form.To"], form.compose input[name="Form.Cc"], form.compose input[name="Form.Bcc"]');
    if (!fields.length || typeof fetch !== 'function') { return; }
    Array.prototype.forEach.call(fields, function (input) {
        input.setAttribute('autocomplete', 'off');
        var list = document.createElement('ul');
        list.className = 'suggestions';
        list.hidden = true;
        list.setAttribute('role', 'listbox');
        input.parentNode.appendChild(list);
        var timer = null, items = [], active = -1, request = 0;

        function current() {
            var parts = input.value.split(',');
            return parts[parts.length - 1].trim();
        }
        function close() {
            list.hidden = true;
            list.replaceChildren();
            items = [];
            active = -1;
        }
        function choose(item) {
            var parts = input.value.split(',');
            parts[parts.length - 1] = item.display;
            input.value = parts.map(function (p) { return p.trim(); }).filter(function (p) { return p.length > 0; }).join(', ') + ', ';
            close();
            input.focus();
        }
        function highlight(index) {
            active = index;
            Array.prototype.forEach.call(list.children, function (li, n) {
                li.classList.toggle('active', n === index);
                li.setAttribute('aria-selected', n === index ? 'true' : 'false');
            });
        }
        function render() {
            list.replaceChildren();
            items.forEach(function (item) {
                var li = document.createElement('li');
                li.setAttribute('role', 'option');
                var name = document.createElement('strong');
                name.textContent = item.name || item.address;
                li.appendChild(name);
                if (item.name) {
                    var address = document.createElement('span');
                    address.textContent = item.address;
                    li.appendChild(address);
                }
                // mousedown instead of click: the field must not lose the focus first.
                li.addEventListener('mousedown', function (e) { e.preventDefault(); choose(item); });
                list.appendChild(li);
            });
            list.style.left = input.offsetLeft + 'px';
            list.style.width = input.offsetWidth + 'px';
            list.hidden = items.length === 0;
            active = -1;
        }
        input.addEventListener('input', function () {
            clearTimeout(timer);
            var term = current();
            if (term.length < 2) { close(); return; }
            timer = setTimeout(function () {
                var id = ++request;
                fetch('/Contacts?handler=Suggest&q=' + encodeURIComponent(term), { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
                    .then(function (r) { return r.ok ? r.json() : []; })
                    .then(function (data) {
                        if (id !== request) { return; }
                        items = Array.isArray(data) ? data : [];
                        render();
                    })
                    .catch(close);
            }, 150);
        });
        input.addEventListener('keydown', function (e) {
            if (list.hidden) { return; }
            if (e.key === 'ArrowDown') { e.preventDefault(); highlight(Math.min(active + 1, items.length - 1)); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); highlight(Math.max(active - 1, 0)); }
            else if ((e.key === 'Enter' || e.key === 'Tab') && active >= 0) { e.preventDefault(); choose(items[active]); }
            else if (e.key === 'Escape') { close(); }
        });
        input.addEventListener('blur', function () { setTimeout(close, 150); });
    });
})();
