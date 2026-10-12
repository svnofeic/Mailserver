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

    textarea.style.display = 'none';
    textarea.parentNode.insertBefore(toolbar, textarea);
    textarea.parentNode.insertBefore(editor, textarea);

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
