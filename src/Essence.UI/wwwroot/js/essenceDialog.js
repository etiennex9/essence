// The About dialog behind AboutDialog.razor: a native modal <dialog>, which closes by itself on Esc and on Android's Back,
// plus a click on the backdrop for browsers without closedby="any". Its changelog loads a month at a time: when the
// "Older versions" button at the end of the list comes near the bottom of the view, .NET is asked for the next month.

const LOAD_AHEAD_PX = 200;

export function attachDialog(dialog, scroller, more, dotnet) {
    let pressedOutside = false;

    // The dialog element itself is the target of clicks on its backdrop, but also on its own edges: compare with its box.
    function isOutside(e) {
        const box = dialog.getBoundingClientRect();
        return e.clientX < box.left || e.clientX > box.right || e.clientY < box.top || e.clientY > box.bottom;
    }

    function onPointerDown(e) {
        pressedOutside = e.target === dialog && isOutside(e);
    }

    // Pressed and released outside, so selecting text and letting go past the edge doesn't close it.
    function onClick(e) {
        if (pressedOutside && e.target === dialog && isOutside(e)) {
            dialog.close();
        }

        pressedOutside = false;
    }

    // Also called after each render: the observer only reports changes, and a short month can leave the button in view.
    function check() {
        if (!dialog.open || more.hidden || more.getAttribute("aria-busy") === "true") {
            return;
        }

        if (more.getBoundingClientRect().top < scroller.getBoundingClientRect().bottom + LOAD_AHEAD_PX) {
            dotnet.invokeMethodAsync("LoadMore").catch(e => console.error(e));
        }
    }

    const observer = new IntersectionObserver(
        entries => {
            if (entries.some(e => e.isIntersecting)) {
                check();
            }
        },
        { root: scroller, rootMargin: `0px 0px ${LOAD_AHEAD_PX}px 0px` });
    observer.observe(more);
    dialog.addEventListener("pointerdown", onPointerDown);
    dialog.addEventListener("click", onClick);

    return {
        open() {
            if (!dialog.open) {
                dialog.showModal();
                // Once shown: while it's display: none, the browser keeps the old offset and puts it back.
                scroller.scrollTop = 0;
            }
        },
        close() {
            if (dialog.open) {
                dialog.close();
            }
        },
        refresh: check,
        dispose() {
            observer.disconnect();
            dialog.removeEventListener("pointerdown", onPointerDown);
            dialog.removeEventListener("click", onClick);
        },
    };
}
