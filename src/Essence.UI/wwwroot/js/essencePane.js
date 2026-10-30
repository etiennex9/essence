// The bottom sheet behind Pane.razor on narrow screens. Dragging its header (or the bar above it) resizes it between its
// default size and an expanded one, or slides it away to close it. Wide screens show a side panel, which doesn't drag.
// Either one's body also scrolls up to an element on request (reveal).

const SNAP_MS = 200;
const DRAG_SLOP_PX = 4; // movement before a press on the header counts as a drag
const DISMISS_SHARE = 0.4; // pulled down past this share of the default height: close
const FLICK_PX_PER_MS = 0.5; // a release faster than this goes the way it was thrown
const VELOCITY_WINDOW_MS = 100;
const STUCK_RESET_MS = 1000;
const REVEAL_MARGIN_PX = 8; // room left above an element scrolled up to

const narrow = window.matchMedia("(max-width: 767px)");
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

export function attachSheet(pane, dotnet) {
    const body = pane.querySelector(".body");
    let drag = null;
    let settleTimer = 0;

    // The max-height the CSS gives each size (the expanded one is the is-expanded class), read with the class flipped
    // for a moment: the style is recomputed synchronously, nothing is painted in between.
    function capOf(expanded) {
        const was = pane.classList.contains("is-expanded");
        pane.classList.toggle("is-expanded", expanded);
        const cap = parseFloat(getComputedStyle(pane).maxHeight);
        pane.classList.toggle("is-expanded", was);
        return Number.isFinite(cap) ? cap : window.innerHeight;
    }

    // Height of everything in the pane if nothing were cut: its chrome plus the scrolling body's full content.
    function naturalHeight() {
        return pane.offsetHeight - body.clientHeight + body.scrollHeight;
    }

    function clearInline() {
        pane.style.removeProperty("height");
        pane.style.removeProperty("max-height");
        pane.style.removeProperty("transform");
        pane.style.removeProperty("transition");
    }

    // Above the default size the height follows the finger; below it the sheet slides down whole.
    function place(height) {
        const { defaultHeight, maxHeight } = drag;
        if (height >= defaultHeight) {
            pane.style.height = `${Math.min(height, maxHeight)}px`;
            pane.style.transform = "";
        } else {
            pane.style.height = `${defaultHeight}px`;
            pane.style.transform = `translateY(${defaultHeight - height}px)`;
        }
    }

    function animate(apply, done) {
        if (reducedMotion.matches) {
            apply();
            done();
            return;
        }

        pane.style.transition = `height ${SNAP_MS}ms ease-out, transform ${SNAP_MS}ms ease-out`;
        void pane.offsetHeight; // start the transition from the dragged position
        apply();
        clearTimeout(settleTimer);
        settleTimer = setTimeout(done, SNAP_MS);
    }

    function settle(expanded, target) {
        animate(
            () => {
                pane.style.height = `${target}px`;
                pane.style.transform = "";
            },
            () => {
                // Back to CSS, so the size follows the content again (prices loading, a station removed…).
                pane.classList.toggle("is-expanded", expanded);
                clearInline();
            });
    }

    function dismiss() {
        animate(
            () => {
                pane.style.transform = "translateY(100%)";
            },
            () => {
                dotnet.invokeMethodAsync("OnSheetDismissed").catch(e => console.error(e));
                // Normally the pane is gone by now; if closing didn't happen, don't leave it off screen.
                settleTimer = setTimeout(() => {
                    if (pane.isConnected) {
                        clearInline();
                    }
                }, STUCK_RESET_MS);
            });
    }

    // Pixels per millisecond over the last moves; positive is downward.
    function velocity(samples) {
        const last = samples[samples.length - 1];
        const first = samples.find(s => last.t - s.t <= VELOCITY_WINDOW_MS) ?? last;
        return last.t > first.t ? (last.y - first.y) / (last.t - first.t) : 0;
    }

    function onPointerDown(e) {
        // Not from a control, nor from a menu or bubble open in the header (essencePopup.js), which scrolls or selects.
        if (!narrow.matches || e.button !== 0 || !e.target.closest("header, .handle")
            || e.target.closest("button, a, input, [data-ess-popup]")) {
            return;
        }

        // A finger is captured by the element it touched, a mouse or pen isn't: capture them too, so the moves keep
        // coming here once the pointer is over the map.
        pane.setPointerCapture(e.pointerId);
        const natural = naturalHeight();
        const defaultHeight = Math.min(natural, capOf(false));
        drag = {
            id: e.pointerId,
            startY: e.clientY,
            startHeight: pane.offsetHeight,
            defaultHeight,
            maxHeight: Math.max(defaultHeight, Math.min(natural, capOf(true))),
            moved: false,
            samples: [{ t: e.timeStamp, y: e.clientY }],
        };
    }

    function onPointerMove(e) {
        if (!drag || e.pointerId !== drag.id) {
            return;
        }

        const dy = e.clientY - drag.startY;
        if (!drag.moved) {
            if (Math.abs(dy) < DRAG_SLOP_PX) {
                return;
            }

            drag.moved = true;
            clearTimeout(settleTimer);
            pane.style.transition = "none";
            pane.style.maxHeight = "none";
        }

        drag.samples.push({ t: e.timeStamp, y: e.clientY });
        if (drag.samples.length > 20) {
            drag.samples.shift();
        }

        place(drag.startHeight - dy);
    }

    function onPointerUp(e) {
        if (!drag || e.pointerId !== drag.id) {
            return;
        }

        const { moved, startY, startHeight, defaultHeight, maxHeight, samples } = drag;
        drag = null;
        if (!moved) {
            return;
        }

        // A cancelled pointer (the system took the gesture) has no meaningful position: settle where it last was.
        const cancelled = e.type === "pointercancel";
        const y = cancelled ? samples[samples.length - 1].y : e.clientY;
        const height = startHeight - (y - startY);
        const speed = cancelled ? 0 : velocity([...samples, { t: e.timeStamp, y }]);

        const pulledDown = height < defaultHeight * (1 - DISMISS_SHARE);
        const flungDown = speed > FLICK_PX_PER_MS && height <= defaultHeight + 1;
        if (pulledDown || flungDown) {
            dismiss();
            return;
        }

        const expanded = speed > FLICK_PX_PER_MS ? false
            : speed < -FLICK_PX_PER_MS ? true
            : height - defaultHeight > maxHeight - height;
        const canExpand = maxHeight > defaultHeight;
        settle(expanded && canExpand, expanded && canExpand ? maxHeight : defaultHeight);
    }

    function onWidthChange() {
        // The side panel has its own size.
        if (!narrow.matches) {
            pane.classList.remove("is-expanded");
            clearInline();
        }
    }

    pane.addEventListener("pointerdown", onPointerDown);
    pane.addEventListener("pointermove", onPointerMove);
    pane.addEventListener("pointerup", onPointerUp);
    pane.addEventListener("pointercancel", onPointerUp);
    narrow.addEventListener("change", onWidthChange);

    return {
        dispose() {
            clearTimeout(settleTimer);
            pane.removeEventListener("pointerdown", onPointerDown);
            pane.removeEventListener("pointermove", onPointerMove);
            pane.removeEventListener("pointerup", onPointerUp);
            pane.removeEventListener("pointercancel", onPointerUp);
            narrow.removeEventListener("change", onWidthChange);
        },
    };
}

// Scrolls the pane's body up to an element above what it shows (the hero, once a station far down a list is chosen),
// smoothly unless motion is reduced: the sheet's on phones, the side panel's under a long trip's every station. Only the
// body scrolls, not the page.
export function reveal(element) {
    const body = element?.closest(".body");
    if (!body) {
        return;
    }

    const delta = element.getBoundingClientRect().top - body.getBoundingClientRect().top;
    if (delta < 0) {
        body.scrollTo({ top: body.scrollTop + delta - REVEAL_MARGIN_PX, behavior: reducedMotion.matches ? "auto" : "smooth" });
    }
}
