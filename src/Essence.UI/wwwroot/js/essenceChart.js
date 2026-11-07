// Says where the pointer is along a price chart, for PriceHistoryChart.razor, as a share of its width: Blazor's pointer events
// don't give that, and a finger is captured by what it touched, so no part of the chart hears it pass. A mouse or pen is
// followed while it's over the chart. A finger is followed from its press, and what it chose stays when it lifts, until a click
// elsewhere or the browser taking the gesture to scroll (touch-action: pan-y).

export function attachChart(chart, dotnet) {
    const send = share => dotnet.invokeMethodAsync("OnPointerAt", share).catch(e => console.error(e));

    // Whether the press a click ends began on the chart, wherever the click lands.
    let pressedOnChart = false;

    function report(e) {
        const box = chart.getBoundingClientRect();
        send((e.clientX - box.left) / box.width);
    }

    function arm() {
        pressedOnChart = true;
        document.addEventListener("pointerdown", onDocumentPointerDown, true);
        document.addEventListener("click", onDocumentClick, true);
    }

    function disarm() {
        document.removeEventListener("pointerdown", onDocumentPointerDown, true);
        document.removeEventListener("click", onDocumentClick, true);
    }

    function clear() {
        disarm();
        send(null);
    }

    function onDocumentPointerDown(e) {
        pressedOnChart = chart.contains(e.target);
    }

    // On the click, not the press: the readout's height changes with what it shows, which moves what's tapped. That also
    // moves the chart from under the finger that just tapped it, so where the press began counts, not where the click lands.
    // A click from the keyboard (no detail) is elsewhere.
    function onDocumentClick(e) {
        if (e.detail === 0 || !pressedOnChart) {
            clear();
        }
    }

    function onPointerDown(e) {
        if (e.isPrimary && e.pointerType === "touch") {
            report(e);
            arm();
        }
    }

    function onPointerMove(e) {
        if (e.isPrimary) {
            report(e);
        }
    }

    // A finger leaves each time it lifts.
    function onPointerLeave(e) {
        if (e.isPrimary && e.pointerType !== "touch") {
            send(null);
        }
    }

    function onPointerCancel(e) {
        if (e.isPrimary) {
            clear();
        }
    }

    chart.addEventListener("pointerdown", onPointerDown);
    chart.addEventListener("pointermove", onPointerMove);
    chart.addEventListener("pointerleave", onPointerLeave);
    chart.addEventListener("pointercancel", onPointerCancel);

    return {
        dispose() {
            chart.removeEventListener("pointerdown", onPointerDown);
            chart.removeEventListener("pointermove", onPointerMove);
            chart.removeEventListener("pointerleave", onPointerLeave);
            chart.removeEventListener("pointercancel", onPointerCancel);
            disarm();
        },
    };
}
