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
