// The Landing module's page script (BL-98 increment 2), loaded by the framework as a <page-script>: onLoad runs on the first
// load and onUpdate after every enhanced navigation. The home page's landing component is STATIC - a visitor's view of it
// opens no circuit - so a dashboard shown there cannot draw its charts through Blazor's interop; this script draws them in
// the browser from what each chart element carries. It loads the chart runtime only when the page holds a chart, so a home
// page without one pays nothing but this file.
const runtimeScripts = ['_content/Vizor.ECharts/js/vizor-echarts-bundle-min.js', 'Modules/Dashboards/dashboards-charts.js'];
let loading = null;

function load(src) {
    const url = new URL(src, document.baseURI).href;
    return new Promise(function (resolve, reject) {
        const script = document.createElement('script');
        script.src = url;
        script.onload = function () { resolve(); };
        script.onerror = function () { reject(new Error('Could not load ' + src)); };
        document.body.appendChild(script);
    });
}

function runtime() {
    if (typeof window.echarts !== 'undefined' && window.stommeCharts && window.stommeCharts.renderAll) {
        return Promise.resolve();
    }

    if (!loading) {
        loading = (typeof window.echarts !== 'undefined' ? Promise.resolve() : load(runtimeScripts[0]))
            .then(function () { return window.stommeCharts && window.stommeCharts.renderAll ? null : load(runtimeScripts[1]); });
        loading.catch(function () { loading = null; });
    }

    return loading;
}

function draw() {
    if (!document.querySelector('[data-db-chart-spec]')) {
        return;
    }

    runtime()
        .then(function () { window.stommeCharts.renderAll(document); })
        .catch(function () {
            document.querySelectorAll('[data-db-chart-spec] + .db-widget-chart-problem').forEach(function (note) {
                note.textContent = 'The chart runtime could not be loaded.';
                note.hidden = false;
            });
        });
}

export function onLoad() {
    draw();
}

export function onUpdate() {
    draw();
}
