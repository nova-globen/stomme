// The Dashboards module's chart interop (BL-96 increment 9, lifted from the predecessor platform's predecessor-charts.js). The ECharts
// runtime ships from the Apache-2.0 Vizor.ECharts package's bundled asset (it assigns the global `echarts`); this
// script renders a widget's theme-free ChartSpec JSON, injects the colour and font palette at render time from the
// active theme (the --db-* tokens when present, a built-in light or dark palette otherwise), and binds "$Column"
// string tokens to that column's values from the widget's bounded preview rows. A STATIC board (the landing page, BL-98)
// has no interop to call it: its chart elements carry the spec, the columns and the rows, and renderAll draws them.
window.stommeCharts = (function () {
    const instances = new Map();

    function lightPalette() {
        return { color: ['#5B63D3', '#16a34a', '#d97706', '#dc2626', '#7c3aed', '#0891b2', '#db2777'], textColor: '#1f2937', axisColor: '#9ca3af', splitColor: '#e5e7eb' };
    }

    function darkPalette() {
        return { color: ['#8b91e6', '#4ade80', '#fbbf24', '#f87171', '#a78bfa', '#22d3ee', '#f472b6'], textColor: '#e5e7eb', axisColor: '#6b7280', splitColor: '#374151' };
    }

    function cssVar(styles, name) {
        return (styles.getPropertyValue(name) || '').trim();
    }

    function resolveTheme() {
        const mode = document.documentElement.getAttribute('data-theme');
        const fallback = mode === 'dark' ? darkPalette() : lightPalette();
        const styles = getComputedStyle(document.documentElement);
        const color = [];
        for (let i = 1; i <= 30; i++) {
            const v = cssVar(styles, '--db-chart-' + i);
            if (!v) break;
            color.push(v);
        }

        return {
            color: color.length ? color : fallback.color,
            textColor: cssVar(styles, '--db-text') || fallback.textColor,
            axisColor: cssVar(styles, '--db-text-2') || fallback.axisColor,
            splitColor: cssVar(styles, '--db-border') || fallback.splitColor
        };
    }

    function applyTheme(option, palette) {
        if (!option.color) option.color = palette.color;
        // ECharts' default grid clips long axis labels in a narrow widget and its 60px margins waste most of a short one:
        // when the theme-free spec positions no grid of its own, reserve label space and use compact margins.
        if (!option.grid) option.grid = { left: 8, right: 16, top: 24, bottom: 8, containLabel: true };
        option.backgroundColor = 'transparent';
        option.textStyle = Object.assign({ color: palette.textColor }, option.textStyle || {});
        ['xAxis', 'yAxis'].forEach(function (key) {
            if (!option[key]) return;
            const axes = Array.isArray(option[key]) ? option[key] : [option[key]];
            axes.forEach(function (axis) {
                if (!axis.axisLine) axis.axisLine = { lineStyle: { color: palette.axisColor } };
                if (!axis.splitLine) axis.splitLine = { lineStyle: { color: palette.splitColor } };
            });
        });
        return option;
    }

    // Replace any "$Column" string token with that column's value array from the fetched rows.
    function bindData(option, columns, rows) {
        if (!rows || !rows.length || !columns || !columns.length) return option;
        const json = JSON.stringify(option, function (key, value) {
            if (typeof value === 'string' && value.charAt(0) === '$') {
                const col = value.substring(1);
                if (columns.indexOf(col) >= 0) {
                    return rows.map(function (r) { return r[col]; });
                }
            }
            return value;
        });
        return JSON.parse(json);
    }

    // Returns a reason when nothing was drawn, null when the chart rendered: the caller shows the reason instead of
    // throwing across the interop boundary (the predecessor platform's seeded sample chart threw a JSException here).
    function render(elementId, specJson, columns, rowsJson) {
        if (typeof echarts === 'undefined') return 'The chart runtime is not loaded.';
        if (!specJson) return 'This chart has no spec.';
        const el = document.getElementById(elementId);
        if (!el) return 'The chart element is not on the page.';

        let option;
        try { option = JSON.parse(specJson); } catch (e) { return 'The chart spec is not valid JSON.'; }

        let rows = [];
        if (rowsJson) {
            try { const parsed = JSON.parse(rowsJson); rows = Array.isArray(parsed) ? parsed : []; }
            catch (e) { rows = []; }
        }

        try {
            option = applyTheme(bindData(option, columns, rows), resolveTheme());
            let chart = instances.get(elementId);
            if (chart && chart.getDom() !== el) {
                // The element was replaced (an enhanced navigation re-renders a static page): draw on the new one.
                chart.dispose();
                instances.delete(elementId);
                chart = null;
            }
            if (!chart) {
                chart = echarts.init(el);
                instances.set(elementId, chart);
                window.addEventListener('resize', function () { chart.resize(); });
            }
            chart.setOption(option, true);
            chart.resize();
            return null;
        } catch (e) {
            return 'The chart could not be drawn: ' + (e && e.message ? e.message : String(e));
        }
    }

    function dispose(elementId) {
        const chart = instances.get(elementId);
        if (chart) { chart.dispose(); instances.delete(elementId); }
    }

    // Draws every chart a static board carries under root, and shows a chart's reason in the note that follows it.
    function renderAll(root) {
        const cells = (root || document).querySelectorAll('[data-db-chart-spec]');
        cells.forEach(function (el) {
            let columns = [];
            try { columns = JSON.parse(el.getAttribute('data-db-chart-columns') || '[]'); } catch (e) { columns = []; }
            const problem = render(el.id, el.getAttribute('data-db-chart-spec') || '', columns, el.getAttribute('data-db-chart-rows'));
            const note = el.nextElementSibling;
            if (note && note.classList.contains('db-widget-chart-problem')) {
                note.textContent = problem || '';
                note.hidden = !problem;
            }
        });
        return cells.length;
    }

    return { render: render, renderAll: renderAll, dispose: dispose };
})();
