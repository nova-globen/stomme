// The pipeline editor's keyboard. Plain script, loaded as a module resource of the editor, so it works the same on
// a Server and a WebAssembly site. The listener is attached once per page, whatever re-renders; the editor registers itself
// (stommePipelineEditorAttach) to receive the editing shortcuts and unregisters when it goes.
(function () {
    if (window.stommePipelineEditorKeys) {
        return;
    }

    window.stommePipelineEditorKeys = true;
    window.stommePipelineEditorRef = null;

    window.stommePipelineEditorAttach = function (ref) {
        window.stommePipelineEditorRef = ref;
        return true;
    };

    window.stommePipelineEditorDetach = function (ref) {
        if (!ref || window.stommePipelineEditorRef === ref) {
            window.stommePipelineEditorRef = null;
        }
    };

    // A part of the inspector brought into sight - a step test's result, after the step's ▶ on the canvas.
    window.stommePipelineEditorReveal = function (id) {
        var element = document.getElementById(id);
        if (element && element.scrollIntoView) {
            element.scrollIntoView({ block: 'nearest' });
        }
    };

    // Shift+F10 or the menu key opened the editor's menu: its first entry takes the focus, and the browser's own menu - which the
    // key may raise after it - is not shown over it.
    var keyboardMenuAt = 0;
    window.stommePipelineEditorFocusMenu = function () {
        var first = document.querySelector('#pipelines-editor-menu [role=menuitem]');
        if (first) {
            first.focus();
        }
    };

    window.addEventListener('contextmenu', function (e) {
        if (Date.now() - keyboardMenuAt < 800) {
            e.preventDefault();
            e.stopImmediatePropagation();
        }
    }, true);

    // The right-click menu's Rename: the step's name, ready to type over.
    window.stommePipelineEditorFocus = function (id) {
        var element = document.getElementById(id);
        if (element && element.focus) {
            element.focus();
            if (element.select) {
                element.select();
            }
        }
    };

    // The right-click menu's Copy: what Ctrl+C puts on the system clipboard, for any pipeline editor.
    window.stommePipelineEditorWriteClipboard = function (text) {
        if (text && navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).catch(function () { });
        }
    };

    // Export - the pipeline file made in the page, saved by the browser; nothing goes back to the server.
    window.stommePipelineEditorDownload = function (fileName, text, type) {
        var url = URL.createObjectURL(new Blob([text], { type: type || 'application/json' }));
        var link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        link.style.display = 'none';
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
    };

    // The diagram library re-measures its box when the WINDOW scrolls (Z.Blazor.Diagrams 3.0.4.1's script.js), but the
    // editor sits in the admin dialog, which scrolls itself, and an element's scroll never reaches the window: the box stayed
    // where it was measured, and every pointer the canvas turned into a spot was off by the dialog's scroll (measured: a note
    // added from the right-click menu landed 85px above the click; a dropped step, a connection let go on empty canvas and a box
    // selection are the same arithmetic). A scroll anywhere, caught on its way down, re-measures each canvas the library's own
    // way - when its box moved, at most every 50 ms.
    var measuring = false;
    document.addEventListener('scroll', function (e) {
        if (measuring || e.target === document) {
            return; // the window's own scroll: the library's
        }

        measuring = true;
        setTimeout(function () {
            measuring = false;
            var library = window.ZBlazorDiagrams;
            if (!library || !library.canvases) {
                return;
            }

            for (var id in library.canvases) {
                var canvas = library.canvases[id];
                var bounds = canvas.elem.getBoundingClientRect();
                var last = canvas.lastBounds;
                if (!last || last.left !== bounds.left || last.top !== bounds.top || last.width !== bounds.width || last.height !== bounds.height) {
                    canvas.lastBounds = bounds;
                    canvas.ref.invokeMethodAsync('OnResize', bounds);
                }
            }
        }, 50);
    }, true);

    function typing(target) {
        if (!target) {
            return false;
        }

        var tag = (target.tagName || '').toLowerCase();
        return tag === 'input' || tag === 'textarea' || tag === 'select' || target.isContentEditable;
    }

    function textSelected() {
        var selection = window.getSelection ? window.getSelection() : null;
        return !!selection && selection.toString().length > 0;
    }

    // A control that answers Enter or the arrows itself - a button, a link, a summary, a list - keeps them; so does the page,
    // to scroll, while no step is selected.
    function interactive(target) {
        return typing(target) || !!(target && target.closest
            && target.closest('button, a[href], summary, [role=button], [role=option], [role=tab], [role=menuitem], [role=listbox], [role=slider]'));
    }

    function stepSelected() {
        return !!document.querySelector('.db-step-node.is-selected');
    }

    // A code box edits like code (n8n's code editor): Tab indents - the lines a selection touches, or two spaces at the
    // caret - and Shift+Tab takes the indent back; Enter keeps the line's indentation, one more after an opening bracket, and a
    // closing bracket right after the caret goes to a line of its own. Escape, then Tab, leaves the box, so the keyboard is never
    // trapped in it. Where the caret is, as a line and a column - the way a script's problem is said - is shown under the box.
    // The edits are pure functions of the text and the selection (stommeCodeBox), so they are tested apart from a page.
    var INDENT = '  ';

    function lineStartOf(value, at) {
        // lastIndexOf from -1 still looks at the first character, so the text's start is said, not searched for.
        return at <= 0 ? 0 : value.lastIndexOf('\n', at - 1) + 1;
    }

    // The lines a selection touches: from the start of its first line to the end of its last - not a line it only reaches the
    // start of, as a selection of whole lines ends there.
    function touchedLines(value, start, end) {
        var first = lineStartOf(value, start);
        var last = end > start && value.charAt(end - 1) === '\n' ? end - 1 : end;
        var close = value.indexOf('\n', last);
        return { from: first, to: close < 0 ? value.length : close };
    }

    window.stommeCodeBox = {
        indent: function (value, start, end) {
            if (value.substring(start, end).indexOf('\n') < 0) {
                return { value: value.substring(0, start) + INDENT + value.substring(end), start: start + INDENT.length, end: start + INDENT.length };
            }

            var lines = touchedLines(value, start, end);
            var block = value.substring(lines.from, lines.to).split('\n');
            var indented = block.map(function (line) { return INDENT + line; }).join('\n');
            return { value: value.substring(0, lines.from) + indented + value.substring(lines.to), start: start + INDENT.length, end: end + INDENT.length * block.length };
        },

        outdent: function (value, start, end) {
            var lines = touchedLines(value, start, end);
            var block = value.substring(lines.from, lines.to).split('\n');
            var removed = block.map(function (line) {
                var spaces = /^ */.exec(line)[0].length;
                return line.charAt(0) === '\t' ? 1 : Math.min(spaces, INDENT.length);
            });
            var outdented = block.map(function (line, at) { return line.substring(removed[at]); }).join('\n');
            var total = removed.reduce(function (sum, count) { return sum + count; }, 0);
            return {
                value: value.substring(0, lines.from) + outdented + value.substring(lines.to),
                start: Math.max(lines.from, start - removed[0]),
                end: Math.max(lines.from, end - total),
            };
        },

        newline: function (value, start, end) {
            var line = value.substring(lineStartOf(value, start), start);
            var indent = /^[ \t]*/.exec(line)[0];
            var before = line.replace(/\s+$/, '').slice(-1);
            var after = value.charAt(end);
            var opens = '{[('.indexOf(before);
            var inserted = '\n' + indent + (opens >= 0 ? INDENT : '');
            var caret = start + inserted.length;
            if (opens >= 0 && after === '}])'.charAt(opens)) {
                inserted += '\n' + indent; // {|} - the closing bracket on a line of its own, at the opening one's indent
            }

            return { value: value.substring(0, start) + inserted + value.substring(end), start: caret, end: caret };
        },

        position: function (value, at) {
            var line = value.substring(0, at).split('\n').length;
            return { line: line, column: at - lineStartOf(value, at) + 1 };
        },

        tokens: function (text, language) {
            return tokens(text, language);
        },

        highlight: function (text, language) {
            return highlight(text, language);
        },

        completions: function (text, caret, names, asked) {
            return completions(text, caret, names, asked);
        },

        // The text a completion chosen makes, and where the caret goes.
        complete: function (text, caret, from, item) {
            var written = completionText(item);
            var end = from + written.text.length - written.back;
            return { value: text.substring(0, from) + written.text + text.substring(caret), start: end, end: end };
        },
    };

    // A code box's code coloured as n8n's code editor colours it: keywords, strings, numbers, comments, and the names a
    // script reads that are not its own (n8n's $json, $input; a statement's @name). The box stays the plain text field it is -
    // its text drawn again, coloured, behind it (the box's own text made clear) - so typing, undo and the page's binding are as
    // they were; until the colours are drawn the box shows its text itself.
    var JS_WORDS = ('await break case catch class const continue debugger default delete do else export extends false finally for ' +
        'function if import in instanceof let new null of return super switch this throw true try typeof undefined var void while ' +
        'with yield async').split(' ');
    var SQL_WORDS = ('select from where and or not insert into values update set delete join left right inner outer full cross on ' +
        'as group by order having limit offset distinct union all is null like ilike in between case when then else end create ' +
        'table drop alter returning top with exists asc desc true false').split(' ');
    var RULES = {
        javascript: [
            ['comment', /\/\/[^\n]*|\/\*[\s\S]*?(?:\*\/|$)/y],
            ['string', /'(?:\\[\s\S]|[^'\\\n])*'?|"(?:\\[\s\S]|[^"\\\n])*"?|`(?:\\[\s\S]|[^`\\])*`?/y],
            ['number', /(?:0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)(?![\w$])/y],
            ['word', /[A-Za-z_$][\w$]*/y],
        ],
        sql: [
            ['comment', /--[^\n]*|\/\*[\s\S]*?(?:\*\/|$)/y],
            ['string', /'(?:''|[^'])*'?/y],
            ['variable', /@[A-Za-z_][\w]*/y],
            ['number', /\d+(?:\.\d+)?(?![\w])/y],
            ['word', /[A-Za-z_][\w$]*/y],
        ],
    };

    // The text cut into [kind, text] pieces, every character in one - kinds: keyword, string, number, comment, variable, plain.
    function tokens(text, language) {
        var rules = RULES[language] || RULES.javascript;
        var words = language === 'sql' ? SQL_WORDS : JS_WORDS;
        var found = [];
        var plain = '';
        var at = 0;
        while (at < text.length) {
            var matched = null;
            for (var r = 0; r < rules.length && !matched; r++) {
                rules[r][1].lastIndex = at;
                var m = rules[r][1].exec(text);
                if (m && m[0].length > 0) {
                    matched = [rules[r][0], m[0]];
                }
            }

            if (matched && matched[0] === 'word') {
                var word = language === 'sql' ? matched[1].toLowerCase() : matched[1];
                matched[0] = words.indexOf(word) >= 0 ? 'keyword' : matched[1].charAt(0) === '$' ? 'variable' : 'plain';
            }

            if (!matched) {
                matched = ['plain', text.charAt(at)];
            }

            if (matched[0] === 'plain') {
                plain += matched[1];
            } else {
                if (plain) {
                    found.push(['plain', plain]);
                    plain = '';
                }

                found.push(matched);
            }

            at += matched[1].length;
        }

        if (plain) {
            found.push(['plain', plain]);
        }

        return found;
    }

    // n8n's completion in its Code node: what may be written where the caret is. NAMES are what the editor knows of the
    // script's world - { name, kind, detail, of, args }: its own names (kind 'global'), its variables' ('variable'), the fields of
    // what it receives ('field'), and the members of a name it knows ('member', of that name; args: it takes arguments). After a
    // '.', a known name's members, or else the fields; elsewhere the names and the words the script already holds, once the
    // author has begun a word - or at once when ASKED (Ctrl+Space). Nothing inside a string or a comment. At most eight.
    var COMPLETIONS_SHOWN = 8;
    var KIND_ORDER = { member: 0, field: 1, variable: 2, global: 3, word: 4 };

    function completions(text, caret, names, asked) {
        var before = text.substring(0, caret);
        var typed = /[A-Za-z_$][\w$]*$/.exec(before);
        var prefix = typed ? typed[0] : '';
        var from = caret - prefix.length;
        var none = { from: from, prefix: prefix, items: [] };
        var pieces = tokens(before, 'javascript');
        var last = pieces.length > 0 ? pieces[pieces.length - 1] : null;
        if (last && (last[0] === 'comment' || (last[0] === 'string' && !/^(['"`])[\s\S]*\1$/.test(last[1])))) {
            return none; // inside a string or a comment the script is not being written
        }

        var member = from > 0 && text.charAt(from - 1) === '.';
        var pool;
        if (member) {
            var owner = /([A-Za-z_$][\w$]*)\s*\.$/.exec(text.substring(0, from));
            var known = owner ? names.filter(function (name) { return name.kind === 'member' && name.of === owner[1]; }) : [];
            pool = known.length > 0 ? known : names.filter(function (name) { return name.kind === 'field'; });
        } else {
            if (!asked && prefix.length === 0) {
                return none;
            }

            pool = names.filter(function (name) { return name.kind !== 'field' && name.kind !== 'member'; });
            var seen = {};
            pool.forEach(function (name) { seen[name.name] = true; });
            var words = text.match(/[A-Za-z_$][\w$]{2,}/g) || [];
            words.forEach(function (word) {
                if (!seen[word] && word !== prefix && JS_WORDS.indexOf(word) < 0) {
                    seen[word] = true;
                    pool.push({ name: word, kind: 'word', detail: 'in the script' });
                }
            });
        }

        var lower = prefix.toLowerCase();
        var items = pool
            .filter(function (name) { return name.name.toLowerCase().indexOf(lower) === 0 && name.name !== prefix; })
            .sort(function (a, b) {
                var exact = (a.name.indexOf(prefix) === 0 ? 0 : 1) - (b.name.indexOf(prefix) === 0 ? 0 : 1);
                return exact || (KIND_ORDER[a.kind] - KIND_ORDER[b.kind]) || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
            })
            .slice(0, COMPLETIONS_SHOWN);
        return { from: from, prefix: prefix, items: items };
    }

    // What a chosen completion writes, as the editor says (insert: 'parse()', back: 1 puts the caret between the brackets), or
    // else its name.
    function completionText(item) {
        return { text: item.insert || item.name, back: item.back || 0 };
    }

    function escapeHtml(text) {
        return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function highlight(text, language) {
        return tokens(text, language).map(function (piece) {
            return piece[0] === 'plain' ? escapeHtml(piece[1]) : '<span class="db-code-' + piece[0] + '">' + escapeHtml(piece[1]) + '</span>';
        }).join('');
    }

    function codeBox(target) {
        return target && target.tagName && target.tagName.toLowerCase() === 'textarea' && target.classList && target.classList.contains('db-pipeline-editor__code');
    }

    // The edit through the browser's own insertText, so its undo keeps it and the page hears an input event; by hand where the
    // browser has no insertText, with the input event the page listens for.
    function applyEdit(box, edit) {
        var old = box.value;
        var prefix = 0;
        while (prefix < old.length && prefix < edit.value.length && old.charAt(prefix) === edit.value.charAt(prefix)) {
            prefix++;
        }

        var suffix = 0;
        while (suffix < old.length - prefix && suffix < edit.value.length - prefix && old.charAt(old.length - 1 - suffix) === edit.value.charAt(edit.value.length - 1 - suffix)) {
            suffix++;
        }

        var replacement = edit.value.substring(prefix, edit.value.length - suffix);
        box.setSelectionRange(prefix, old.length - suffix);
        var inserted = false;
        applying = true; // an edit of the editor's own: the completion list does not answer it
        try {
            try {
                inserted = document.execCommand && document.execCommand('insertText', false, replacement);
            } catch (err) {
                inserted = false;
            }

            if (!inserted || box.value !== edit.value) {
                box.value = edit.value;
                box.dispatchEvent(new Event('input', { bubbles: true }));
            }
        } finally {
            applying = false;
        }

        box.setSelectionRange(edit.start, edit.end);
        showPosition(box);
    }

    function showPosition(box) {
        var shown = box.id ? document.getElementById(box.id + '-position') : null;
        if (shown) {
            var where = window.stommeCodeBox.position(box.value, box.selectionStart);
            shown.textContent = 'Line ' + where.line + ', column ' + where.column;
        }
    }

    var escapedFrom = null;
    document.addEventListener('keydown', function (e) {
        var box = e.target;
        if (!codeBox(box) || box.disabled || box.readOnly) {
            return;
        }

        // The completion list, while it is open, has the keys that move in it, choose, or close it.
        if (completing && completing.box === box) {
            if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
                e.preventDefault();
                completing.active = (completing.active + (e.key === 'ArrowDown' ? 1 : -1) + completing.items.length) % completing.items.length;
                markActive();
                return;
            }

            if ((e.key === 'Enter' || e.key === 'Tab') && !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey) {
                e.preventDefault();
                acceptCompletion(completing.active);
                return;
            }

            if (e.key === 'Escape') {
                e.preventDefault();
                closeCompletions(); // and not armed to leave the box: Escape closed the list
                return;
            }
        }

        if (e.ctrlKey && !e.metaKey && !e.altKey && (e.key === ' ' || e.code === 'Space')) {
            e.preventDefault();
            openCompletions(box, true); // Ctrl+Space: what may be written here, asked for
            return;
        }

        if (e.ctrlKey || e.metaKey || e.altKey) {
            return;
        }

        if (e.key === 'Escape') {
            escapedFrom = box; // the next Tab leaves the box
            return;
        }

        var leaving = escapedFrom === box;
        escapedFrom = null;
        if (e.key === 'Tab' && !leaving) {
            e.preventDefault();
            applyEdit(box, e.shiftKey
                ? window.stommeCodeBox.outdent(box.value, box.selectionStart, box.selectionEnd)
                : window.stommeCodeBox.indent(box.value, box.selectionStart, box.selectionEnd));
        } else if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            applyEdit(box, window.stommeCodeBox.newline(box.value, box.selectionStart, box.selectionEnd));
        }
    }, true);

    ['keyup', 'click', 'focusin', 'input', 'select'].forEach(function (name) {
        document.addEventListener(name, function (e) {
            if (codeBox(e.target)) {
                showPosition(e.target);
                shade(e.target);
            }
        });
    });

    // The completion list, one for the page, kept on the page's body - the editor's own drawing never meets it - and placed at
    // the caret, measured on the coloured copy, which lays the text out as the box does. A box offers it only when the editor
    // said what its script can see (data-code-names); it opens as a word is typed or after a '.', or on Ctrl+Space.
    var completing = null;
    var applying = false;

    function namesOf(box) {
        try {
            return JSON.parse(box.getAttribute('data-code-names') || '[]');
        } catch (err) {
            return [];
        }
    }

    function caretPoint(box) {
        var wrap = box.parentElement;
        var copy = wrap ? wrap.querySelector('.db-pipeline-editor__code-shade') : null;
        if (copy && wrap.classList.contains('is-shaded') && document.createRange && document.createTreeWalker) {
            var walker = document.createTreeWalker(copy, NodeFilter.SHOW_TEXT);
            var left = box.selectionStart;
            for (var node = walker.nextNode(); node; node = walker.nextNode()) {
                if (left <= node.length) {
                    var range = document.createRange();
                    range.setStart(node, left);
                    range.setEnd(node, left);
                    var rects = range.getClientRects();
                    var at = rects.length > 0 ? rects[0] : range.getBoundingClientRect();
                    if (at && (at.top || at.left)) {
                        return { left: at.left, top: at.bottom };
                    }

                    break;
                }

                left -= node.length;
            }
        }

        var whole = box.getBoundingClientRect();
        return { left: whole.left, top: whole.bottom };
    }

    function placeCompletions() {
        var point = caretPoint(completing.box);
        completing.list.style.left = Math.round(point.left) + 'px';
        completing.list.style.top = Math.round(point.top + 2) + 'px';
        // Above whatever holds the box: one more than the highest layer among its ancestors (the admin dialog is 9999 - found
        // driving it, the list drawn beneath the box it belonged to).
        var highest = 0;
        for (var holder = completing.box.parentElement; holder && holder !== document.body; holder = holder.parentElement) {
            var layer = parseInt(window.getComputedStyle(holder).zIndex, 10);
            if (!isNaN(layer) && layer > highest) {
                highest = layer;
            }
        }

        completing.list.style.zIndex = highest > 0 ? String(highest + 1) : '';
    }

    function closeCompletions() {
        if (!completing) {
            return;
        }

        if (completing.list.parentNode) {
            completing.list.parentNode.removeChild(completing.list);
        }

        completing.box.setAttribute('aria-expanded', 'false');
        completing.box.removeAttribute('aria-activedescendant');
        completing = null;
    }

    function markActive() {
        var options = completing.list.children;
        for (var at = 0; at < options.length; at++) {
            options[at].setAttribute('aria-selected', at === completing.active ? 'true' : 'false');
            options[at].classList.toggle('is-active', at === completing.active);
        }

        completing.box.setAttribute('aria-activedescendant', 'db-code-complete-' + completing.active);
    }

    function openCompletions(box, asked) {
        if (!box.hasAttribute('data-code-names') || box.selectionStart !== box.selectionEnd) {
            closeCompletions();
            return;
        }

        var found = window.stommeCodeBox.completions(box.value, box.selectionStart, namesOf(box), asked);
        if (found.items.length === 0) {
            closeCompletions();
            return;
        }

        var list = completing ? completing.list : document.createElement('ul');
        list.id = 'db-code-complete';
        list.className = 'db-code-complete';
        list.setAttribute('role', 'listbox');
        list.setAttribute('aria-label', 'What may be written here');
        while (list.firstChild) {
            list.removeChild(list.firstChild);
        }

        found.items.forEach(function (item, at) {
            var option = document.createElement('li');
            option.id = 'db-code-complete-' + at;
            option.setAttribute('role', 'option');
            option.className = 'db-code-complete__item';
            var name = document.createElement('span');
            name.className = 'db-code-complete__name';
            name.textContent = item.insert || item.name;
            var detail = document.createElement('span');
            detail.className = 'db-code-complete__detail';
            detail.textContent = item.detail || item.kind;
            option.appendChild(name);
            option.appendChild(detail);
            option.addEventListener('mousedown', function (e) {
                e.preventDefault(); // the box keeps the focus
                acceptCompletion(at);
            });
            list.appendChild(option);
        });

        if (!list.parentNode) {
            document.body.appendChild(list);
        }

        completing = { box: box, from: found.from, items: found.items, active: 0, list: list };
        placeCompletions();
        box.setAttribute('aria-autocomplete', 'list');
        box.setAttribute('aria-controls', list.id);
        box.setAttribute('aria-expanded', 'true');
        markActive();
    }

    function acceptCompletion(at) {
        var chosen = completing;
        if (!chosen || !chosen.items[at]) {
            return;
        }

        closeCompletions();
        applyEdit(chosen.box, window.stommeCodeBox.complete(chosen.box.value, chosen.box.selectionStart, chosen.from, chosen.items[at]));
    }

    // Typing a word, a '.' or a '$' offers what may follow; taking letters back keeps the list to what still fits; anything else
    // - or an edit of the editor's own (Tab, Enter, a completion chosen) - closes it.
    document.addEventListener('input', function (e) {
        var box = e.target;
        if (!codeBox(box) || applying) {
            return;
        }

        // What was typed ends a word or is a '.' - a key, or several letters at once (an input method's, found driving it).
        var typed = e.inputType === 'insertText' && typeof e.data === 'string' && /[\w$.]$/.test(e.data);
        var takenBack = e.inputType === 'deleteContentBackward' && completing && completing.box === box;
        if (typed || takenBack) {
            openCompletions(box, false);
        } else {
            closeCompletions();
        }
    });

    document.addEventListener('focusout', function (e) {
        if (completing && e.target === completing.box) {
            closeCompletions();
        }
    });

    document.addEventListener('mousedown', function (e) {
        if (completing && !(e.target && e.target.closest && e.target.closest('.db-code-complete'))) {
            closeCompletions();
        }
    }, true);

    // A scroll - the box's own as a long line is typed, the dialog's, the page's - moves the caret on the screen: the list
    // follows it (closing it, as it first did, closed the list under a word being typed at the end of a long line).
    document.addEventListener('scroll', function (e) {
        if (completing && e.target !== completing.list) {
            placeCompletions();
        }
    }, true);

    document.addEventListener('keyup', function (e) {
        if (completing && ['ArrowLeft', 'ArrowRight', 'Home', 'End', 'PageUp', 'PageDown'].indexOf(e.key) >= 0) {
            closeCompletions(); // the caret went elsewhere
        }
    });

    // The coloured copy behind a code box: drawn again when its text changed - typed, or set by the page (Undo, the AI, another
    // step chosen), which raises no event, so every code box on the page is looked at a few times a second - and kept to the
    // box's own scroll, font and spacing. The box's text turns clear only once the copy is drawn (is-shaded).
    var METRICS = ['fontFamily', 'fontSize', 'fontWeight', 'lineHeight', 'letterSpacing', 'tabSize', 'paddingTop', 'paddingRight',
        'paddingBottom', 'paddingLeft', 'borderTopWidth', 'borderRightWidth', 'borderBottomWidth', 'borderLeftWidth', 'borderRadius'];

    function shade(box) {
        var wrap = box.parentElement;
        var copy = wrap && wrap.classList.contains('db-pipeline-editor__code-wrap') ? wrap.querySelector('.db-pipeline-editor__code-shade') : null;
        if (!copy) {
            return;
        }

        var language = wrap.getAttribute('data-code-language') || 'javascript';
        if (copy.stommeShaded !== box.value || copy.stommeLanguage !== language) {
            copy.innerHTML = highlight(box.value, language) + '\n'; // a last empty line has a height too
            copy.stommeShaded = box.value;
            copy.stommeLanguage = language;
        }

        var style = window.getComputedStyle(box);
        METRICS.forEach(function (metric) {
            if (copy.style[metric] !== style[metric]) {
                copy.style[metric] = style[metric];
            }
        });
        copy.scrollTop = box.scrollTop;
        copy.scrollLeft = box.scrollLeft;
        wrap.classList.add('is-shaded');
    }

    document.addEventListener('scroll', function (e) {
        if (codeBox(e.target)) {
            shade(e.target);
        }
    }, true);

    setInterval(function () {
        var boxes = document.querySelectorAll('textarea.db-pipeline-editor__code');
        for (var at = 0; at < boxes.length; at++) {
            shade(boxes[at]);
        }
    }, 250);

    document.addEventListener('keydown', function (e) {
        // Ctrl+F (Cmd+F) on the editor: find a step on the canvas - for anyone who can see the pipeline, author or not.
        var find = document.getElementById('pipelines-editor-find');
        if (find && (e.ctrlKey || e.metaKey) && (e.key || '').toLowerCase() === 'f') {
            e.preventDefault();
            find.focus();
            find.select();
            return;
        }

        var search = document.getElementById('pipelines-editor-step-search');
        if (!search || search.disabled) {
            return; // not on the editor, or the editor cannot change the pipeline
        }

        var ctrl = e.ctrlKey || e.metaKey;
        var key = (e.key || '').toLowerCase();

        // Ctrl+K (Cmd+K) from anywhere, or "/" when not typing in a field: find a step.
        if ((ctrl && key === 'k') || (e.key === '/' && !typing(e.target))) {
            e.preventDefault();
            search.focus();
            search.select();
            return;
        }

        var ref = window.stommePipelineEditorRef;
        if (!ref) {
            return;
        }

        // In the right-click menu: Up and Down go through its entries (round), Home and End to the first and the last.
        var entry = e.target && e.target.closest ? e.target.closest('#pipelines-editor-menu [role=menuitem]') : null;
        if (entry && ['ArrowDown', 'ArrowUp', 'Home', 'End'].indexOf(e.key) >= 0) {
            var entries = Array.prototype.slice.call(document.querySelectorAll('#pipelines-editor-menu [role=menuitem]'));
            var at = entries.indexOf(entry);
            var next = e.key === 'Home' ? 0 : e.key === 'End' ? entries.length - 1 : (at + (e.key === 'ArrowDown' ? 1 : -1) + entries.length) % entries.length;
            e.preventDefault();
            entries[next].focus();
            return;
        }

        // Ctrl+S saves and Ctrl+Enter runs even from a field - but in the box that asks the AI, Ctrl+Enter asks it
        // (the box answers that itself); the rest are the canvas's while no field has the focus, and a plain copy of text the
        // person selected on the page stays the browser's.
        var command = null;
        if (ctrl && key === 's') {
            command = 'save';
        } else if (ctrl && key === 'enter' && !(e.target && e.target.id === 'pipelines-editor-ai-instruction')) {
            command = 'run';
        } else if (!typing(e.target)) {
            if (ctrl && !e.shiftKey && key === 'z') {
                command = 'undo';
            } else if (ctrl && (key === 'y' || (e.shiftKey && key === 'z'))) {
                command = 'redo';
            } else if (ctrl && key === 'c' && !textSelected()) {
                // The copy goes to the system clipboard too (a pipeline file of the steps), so it pastes into any pipeline editor.
                e.preventDefault();
                ref.invokeMethodAsync('CopySelection').then(function (text) {
                    if (text && navigator.clipboard && navigator.clipboard.writeText) {
                        navigator.clipboard.writeText(text).catch(function () { });
                    }
                });
                return;
            } else if (ctrl && key === 'x' && !textSelected()) {
                // Ctrl+X: Ctrl+C's copy - to the system clipboard too - and the selected steps deleted.
                e.preventDefault();
                ref.invokeMethodAsync('CutSelection').then(function (text) {
                    if (text && navigator.clipboard && navigator.clipboard.writeText) {
                        navigator.clipboard.writeText(text).catch(function () { });
                    }
                });
                return;
            } else if (ctrl && !e.shiftKey && key === 'd') {
                command = 'duplicate'; // n8n's Ctrl+D
            } else if (ctrl && key === 'a') {
                command = 'selectAll';
            } else if (!ctrl && (key === 'delete' || key === 'backspace')) {
                command = 'delete';
            } else if (!ctrl && !e.altKey && key === 'd') {
                command = 'disable'; // n8n's deactivate: switch the selected steps off, or back on
            } else if (!ctrl && e.key === '?') {
                command = 'help'; // the shortcuts list
            } else if (key === 'escape') {
                command = 'escape';
            } else if (!ctrl && !e.altKey && (e.key === 'ContextMenu' || (e.shiftKey && e.key === 'F10'))) {
                keyboardMenuAt = Date.now();
                command = 'menu'; // the right-click menu from the keyboard
            } else if (!ctrl && !e.altKey && e.key === 'F2') {
                // n8n's F2: the selected step's name, ready to type over.
                var name = document.getElementById('pipelines-editor-step-name');
                if (name && !name.disabled) {
                    e.preventDefault();
                    name.focus();
                    name.select();
                }

                return;
            } else if (!ctrl && !e.altKey && !e.shiftKey && key === 'enter' && !interactive(e.target) && stepSelected()) {
                command = 'open'; // n8n's Enter: the step's details - the wide view
            } else if (!ctrl && !e.altKey && !e.shiftKey && key.indexOf('arrow') === 0 && !interactive(e.target) && stepSelected()) {
                command = key.substring(5); // left, right, up, down: the next step that way
            } else if (!ctrl && !e.altKey && e.shiftKey && key.indexOf('arrow') === 0 && !interactive(e.target) && document.querySelector('.db-step-node.is-selected, .db-note.is-selected')) {
                command = 'nudge' + key.substring(5); // Shift and an arrow: what is chosen moved a dot of the grid that way
            } else if (!ctrl && !e.altKey && e.shiftKey && key === 's') {
                command = 'note'; // n8n's Shift+S: a sticky note
            } else if (!ctrl && e.altKey && e.shiftKey && e.code === 'KeyT') {
                command = 'tidy'; // n8n's Shift+Alt+T: tidy up (the key's code - Alt changes the character on a Mac)
            } else if (!ctrl && e.altKey && !e.shiftKey && e.code === 'KeyX' && stepSelected()) {
                command = 'subPipeline'; // n8n's Alt+X: the selected steps moved into a sub-pipeline of their own
            } else if (!ctrl && !e.altKey && e.key === '1') {
                command = 'fit';
            } else if (!ctrl && !e.altKey && e.key === '0') {
                command = 'zoomReset';
            } else if (!ctrl && !e.altKey && (e.key === '+' || e.key === '=')) {
                command = 'zoomIn';
            } else if (!ctrl && !e.altKey && e.key === '-') {
                command = 'zoomOut';
            }
        }

        if (command) {
            e.preventDefault();
            ref.invokeMethodAsync('OnShortcut', command);
        }
    });

    // n8n's mapping: a variable or a field dragged from the inspector carries its placeholder; dropped on an input's
    // VALUE (a text, number or multi-line field the step reads as typed) the editor makes the value an expression ending in it.
    // Dropped on an expression it is the browser's own insert, at the caret.
    document.addEventListener('dragstart', function (e) {
        var source = e.target && e.target.closest ? e.target.closest('[data-drag-text]') : null;
        if (source && e.dataTransfer) {
            e.dataTransfer.setData('text/plain', source.getAttribute('data-drag-text'));
            e.dataTransfer.effectAllowed = 'copy';
        }
    });

    document.addEventListener('drop', function (e) {
        var target = e.target;
        var ref = window.stommePipelineEditorRef;
        var text = e.dataTransfer ? e.dataTransfer.getData('text/plain') : '';
        var prefix = 'pipelines-editor-input-';
        if (!ref || !target || !target.id || target.id.indexOf(prefix) !== 0 || !/^\{\{[^{}]*\}\}$/.test(text || '')) {
            return;
        }

        var tag = (target.tagName || '').toLowerCase();
        if (tag !== 'textarea' && !(tag === 'input' && (target.type === 'text' || target.type === 'number'))) {
            return;
        }

        e.preventDefault();
        ref.invokeMethodAsync('OnDropIntoInput', target.id.substring(prefix.length), text);
    });

    // Ctrl+V: the browser's own paste, which carries the clipboard's text without asking - steps copied in any pipeline editor
    // paste with the variables they use; anything else falls back to the editor's own copy. A field keeps its paste.
    document.addEventListener('paste', function (e) {
        var search = document.getElementById('pipelines-editor-step-search');
        var ref = window.stommePipelineEditorRef;
        if (!search || search.disabled || !ref || typing(e.target)) {
            return;
        }

        e.preventDefault();
        ref.invokeMethodAsync('OnPasteText', e.clipboardData ? e.clipboardData.getData('text/plain') : '');
    });
})();
