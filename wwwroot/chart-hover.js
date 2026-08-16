// Reading a value off a chart by pointing at it.
//
// Every chart in LabbyTwo is server-rendered SVG with no client state, which is what makes
// them work during prerender and inside a static render. Hovering is the one thing that
// cannot be done that way: it is a pointer position, it changes sixty times a second, and
// asking the server would put a network round trip inside a mouse move. So this is the
// second small script in the app, for the same reason as the first — see widget-drag.js.
//
// Nothing is added inside the <svg>. The crosshair and the tooltip are two elements on
// <body>, positioned over the chart, because the SVG is a tree Blazor rendered and owns:
// putting nodes inside it risks the next diff, and that lesson is already written down in
// widget-drag.js. Here it costs nothing — a line and a box do not need to be in the same
// coordinate space as the chart, only in the same place on screen.
//
// A chart opts in by carrying a data-chart attribute. Its shape:
//
//   { u: " ms", d: 1, t0: 1755300000, t1: 1755386400,
//     s: [ { n: "download", c: "var(--accent)", v: [452, 441, ...] } ] }
//
// The values are already converted into whatever units the user reads in, because the
// server is the only thing that knows that. This only formats and positions.

(function () {
    let cursor = null;
    let tip = null;
    let showing = null;

    function build() {
        if (cursor) return;

        cursor = document.createElement('div');
        cursor.className = 'chart-cursor';
        cursor.setAttribute('aria-hidden', 'true');

        tip = document.createElement('div');
        tip.className = 'chart-tip';
        tip.setAttribute('role', 'tooltip');

        document.body.append(cursor, tip);
    }

    function hide() {
        if (!showing) return;
        showing = null;
        cursor.style.display = 'none';
        tip.style.display = 'none';
    }

    /// Parses the attribute once per hover rather than per move: a 24-hour chart carries a
    /// few thousand numbers and JSON.parse on every pointer event is the one thing here that
    /// could actually be slow.
    function read(svg) {
        if (svg._chart && svg._chartRaw === svg.dataset.chart) return svg._chart;
        try {
            svg._chart = JSON.parse(svg.dataset.chart);
            svg._chartRaw = svg.dataset.chart;
            return svg._chart;
        } catch {
            return null;
        }
    }

    function when(chart, fraction) {
        if (!chart.t0 || !chart.t1) return '';
        const at = new Date((chart.t0 + (chart.t1 - chart.t0) * fraction) * 1000);
        // A window longer than a day needs the day named, or "14:00" is ambiguous across it.
        const long = (chart.t1 - chart.t0) > 86400;
        return at.toLocaleString([], long
            ? { weekday: 'short', hour: '2-digit', minute: '2-digit' }
            : { hour: '2-digit', minute: '2-digit' });
    }

    function move(event) {
        const svg = event.target.closest ? event.target.closest('svg[data-chart]') : null;
        if (!svg) { hide(); return; }

        const chart = read(svg);
        if (!chart || !chart.s || !chart.s.length) { hide(); return; }

        const box = svg.getBoundingClientRect();
        if (box.width <= 0) { hide(); return; }

        // Snapped to the densest series: that is the line with the most detail, and a
        // crosshair that lands on its points looks right against every other.
        const count = Math.max(...chart.s.map(series => series.v.length));
        if (count < 1) { hide(); return; }

        // Snapped to the nearest real reading rather than following the pointer freely: the
        // number under a crosshair that sits between two samples is one that was never taken.
        const fraction = Math.min(1, Math.max(0, (event.clientX - box.left) / box.width));
        const index = count === 1 ? 0 : Math.round(fraction * (count - 1));
        const x = box.left + (count === 1 ? box.width / 2 : (index / (count - 1)) * box.width);

        build();
        showing = svg;

        cursor.style.display = 'block';
        cursor.style.left = `${x}px`;
        cursor.style.top = `${box.top}px`;
        cursor.style.height = `${box.height}px`;

        // Each series is read at its own index rather than a shared one. Some charts stretch
        // every line across the full width whatever its length — a connection added
        // yesterday has fewer points than one running all week — so the same x is a
        // different sample number in each of them.
        const rows = chart.s
            .map(series => ({
                series,
                value: series.v.length < 2
                    ? series.v[0]
                    : series.v[Math.round(fraction * (series.v.length - 1))],
            }))
            .filter(row => row.value !== undefined && row.value !== null);

        if (!rows.length) { hide(); return; }

        // Built as nodes with textContent rather than as a string of HTML. A series name can
        // be a connection's name, which somebody typed — the compare card labels its lines
        // with them — and innerHTML would run whatever they typed. There is no markup here
        // worth the risk of being wrong about that.
        tip.textContent = '';

        const stamp = when(chart, fraction);
        if (stamp) {
            const at = document.createElement('span');
            at.className = 'chart-tip-when';
            at.textContent = stamp;
            tip.appendChild(at);
        }

        for (const { series, value } of rows) {
            const row = document.createElement('span');
            row.className = 'chart-tip-row';

            const key = document.createElement('span');
            key.className = 'chart-tip-key';
            key.style.background = series.c || 'var(--accent)';
            row.appendChild(key);

            if (series.n) {
                const name = document.createElement('span');
                name.className = 'chart-tip-name';
                name.textContent = series.n;
                row.appendChild(name);
            }

            const reading = document.createElement('span');
            reading.className = 'chart-tip-value';
            reading.textContent = value.toFixed(chart.d ?? 0) + (chart.u || '');
            row.appendChild(reading);

            tip.appendChild(row);
        }

        tip.style.display = 'block';

        // Measured after the content is in, because the box is as wide as what it says, and
        // flipped to the other side of the cursor near the right edge so it never runs off.
        const size = tip.getBoundingClientRect();
        const left = x + 12 + size.width > window.innerWidth ? x - 12 - size.width : x + 12;
        tip.style.left = `${Math.max(4, left)}px`;
        tip.style.top = `${Math.max(4, box.top + box.height / 2 - size.height / 2)}px`;
    }

    document.addEventListener('pointermove', move, { passive: true });
    document.addEventListener('pointerleave', hide, { passive: true });

    // A chart can be replaced under a stationary pointer when its card refreshes, and a
    // tooltip left describing a chart that no longer exists is worse than none.
    document.addEventListener('pointerdown', hide, { passive: true });
    window.addEventListener('scroll', hide, { passive: true });
    window.addEventListener('resize', hide, { passive: true });
})();
