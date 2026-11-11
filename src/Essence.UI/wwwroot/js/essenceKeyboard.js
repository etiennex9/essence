// Keeps an element at the bottom of the page above a phone's keyboard while the focus is in it: the dropped pin's card
// (DroppedPin.razor) and its name field. The keyboard shrinks only the visual viewport, so the page under it keeps its
// size, and Firefox, unlike Chrome, doesn't move the view to the field: the card would stay under the keyboard. --keyboard
// is how much of the bottom of the element's positioned parent the keyboard (or anything else) hides, for its CSS to add
// to its bottom; it's 0 while the focus is elsewhere, or when the browser brought the field into view by itself.

export function keepAboveKeyboard(element) {
    const viewport = window.visualViewport;
    if (!viewport) {
        return { dispose() {} };
    }

    let frame = 0;

    function update() {
        frame = 0;
        let hidden = 0;
        if (element.contains(document.activeElement)) {
            const parent = element.offsetParent ?? document.documentElement;
            hidden = Math.round(parent.getBoundingClientRect().bottom - (viewport.offsetTop + viewport.height));
        }

        if (hidden > 0) {
            element.style.setProperty("--keyboard", `${hidden}px`);
        } else {
            element.style.removeProperty("--keyboard");
        }
    }

    // Once a frame, and after a focusout: the focus moving to another field of the element leaves it for a moment.
    function schedule() {
        if (!frame) {
            frame = requestAnimationFrame(update);
        }
    }

    viewport.addEventListener("resize", schedule);
    viewport.addEventListener("scroll", schedule);
    element.addEventListener("focusin", schedule);
    element.addEventListener("focusout", schedule);

    return {
        dispose() {
            cancelAnimationFrame(frame);
            viewport.removeEventListener("resize", schedule);
            viewport.removeEventListener("scroll", schedule);
            element.removeEventListener("focusin", schedule);
            element.removeEventListener("focusout", schedule);
        },
    };
}
