// The Admin Dashboard's search (BL-204), a page script: the framework's <page-script> element imports it on a static page
// and calls onLoad, then onUpdate after every enhanced navigation; on an interactive page ModuleBase imports it and calls
// onUpdate after each later render (BL-202). The dashboard control is static, so the filter cannot be a Blazor event - it
// runs here, over what each tile carries in data-search (its name, title and path, lower-cased). It does nothing on a page
// without the dashboard.
//
// A wired input is marked on the element under a global symbol - not in an attribute, which an enhanced navigation resets to
// the server's while it keeps the element and its listeners, and not in a set of this module's own: on an interactive site
// this static control's script is imported by App.razor's <page-script> AND by ModuleBase (measured: one URL, so one module
// instance today), and two URLs would be two instances, each wiring the same input.
const wired = Symbol.for('stomme.admin.dashboard.search');

function tilesOf(dashboard) {
    return Array.prototype.slice.call(dashboard.querySelectorAll('.db-dash-tile[data-search]'));
}

function apply(dashboard, input) {
    const terms = input.value.toLowerCase().split(/\s+/).filter(function (term) { return term.length > 0; });
    let shown = 0;
    tilesOf(dashboard).forEach(function (tile) {
        const text = tile.getAttribute('data-search') || '';
        const match = terms.every(function (term) { return text.indexOf(term) !== -1; });
        tile.hidden = !match;
        if (match) {
            shown++;
        }
    });

    const empty = dashboard.querySelector('.db-dash-empty');
    if (empty) {
        empty.hidden = shown > 0;
    }
}

function wire(dashboard, input) {
    input.addEventListener('input', function () { apply(dashboard, input); });
    input.addEventListener('keydown', function (event) {
        if (event.key === 'Enter') {
            const visible = tilesOf(dashboard).filter(function (tile) { return !tile.hidden; });
            if (visible.length === 1) {
                event.preventDefault();
                visible[0].click();
            }
        } else if (event.key === 'Escape' && input.value !== '') {
            event.preventDefault();
            input.value = '';
            apply(dashboard, input);
        }
    });
    input[wired] = true;
}

function attach() {
    const dashboard = document.querySelector('.db-dash');
    const input = dashboard && dashboard.querySelector('.db-dash-search input');
    if (!input) {
        return;
    }

    if (!input[wired]) {
        wire(dashboard, input);
        // Keyboard-first: the dashboard opens with its search focused, unless the user is already somewhere else.
        if (!document.activeElement || document.activeElement === document.body) {
            input.focus();
        }
    }

    // An enhanced navigation or a re-render patches the tiles back to what the server sent; the search still applies.
    apply(dashboard, input);
}

export function onLoad() {
    attach();
}

export function onUpdate() {
    attach();
}
