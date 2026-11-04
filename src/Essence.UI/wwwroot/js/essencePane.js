// The bottom sheet behind Pane.razor on narrow screens, at three sizes: its default one, an expanded one, and slim, shrunk
// to its bar and header so the map shows. Dragging its header (or the bar above it) moves it between them, and down again
// from slim slides it away to close it. A tap on the bar switches it between slim and its default size, and a tap on a slim
// sheet's header brings it back. Wide screens show a side panel, which doesn't drag. Either one's body also scrolls up to
// an element on request (reveal). The map (essenceMap.js) keeps clear of what the panes cover (paneCover), hears when that
// changes (PANE_CHANGE_EVENT), and shrinks a sheet when it's tapped (collapseSheets).

const SNAP_MS = 200;
const DRAG_SLOP_PX = 4; // movement before a press on the header counts as a drag
const TAP_SLOP_PX = 10; // a press that moved less than this is still a tap, as a finger's click is
const DISMISS_SHARE = 0.4; // from slim, pulled down past this share of its height: close
const FLICK_PX_PER_MS = 0.5; // a release faster than this goes the way it was thrown
const VELOCITY_WINDOW_MS = 100;
const STUCK_RESET_MS = 1000;
const REVEAL_MARGIN_PX = 8; // room left above an element scrolled up to

// The slim sheet's height, set on the document for what stays above it: the map's zoom buttons and About.
const SLIM_VAR = "--ess-slim-sheet";

// What a press on the header doesn't drag from: its controls, and a menu or bubble open in it (essencePopup.js), which
// scrolls or selects. The bar is a button too, but a drag starts from it.
const CONTROLS = "button:not(.handle), a, input, [data-ess-popup]";

// Sent on the document when a pane opens, closes or changes size; the map then makes room for it.
export const PANE_CHANGE_EVENT = "ess:pane-change";

const wide = window.matchMedia("(min-width: 768px)"); // Pane.razor.css's breakpoint
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

// The panes attached, by their element: how much of the map each covers, and how to shrink it.
const sheets = new Map();

// The sheet that set SLIM_VAR.
let slimSheet = null;

export function attachSheet(pane, dotnet) {
    const body = pane.querySelector(".body");
    const handle = pane.querySelector(".handle");

    // "default", "expanded" or "slim"; target is the size an animation is on its way to.
    let state = pane.classList.contains("is-expanded") ? "expanded" : "default";
    let target = null;
    let drag = null;
    let tap = null; // the last press, when it was a tap: the click it ends with switches or restores the sheet
    let settleTimer = 0;
    let stuckTimer = 0;
    let dismissing = false;
    let lastHeight = -1;

    const size = () => target ?? state;
    const busy = () => settleTimer !== 0 || drag?.moved || dismissing;

    // The height CSS gives each size, read with the classes flipped for a moment and nothing set inline: the style is
    // recomputed synchronously, nothing is painted in between. Only with no transition set inline, which the change back
    // would start. is-measuring keeps a slim sheet's body visible meanwhile, so a focus in it isn't lost.
    function measure() {
        const scrollTop = body.scrollTop;
        const style = pane.getAttribute("style");
        const slim = pane.classList.contains("is-slim");
        const expanded = pane.classList.contains("is-expanded");
        pane.style.transition = "none";
        pane.style.removeProperty("height");
        pane.style.removeProperty("max-height");
        pane.style.removeProperty("transform");
        pane.classList.add("is-measuring");
        const heightAs = name => {
            pane.classList.toggle("is-slim", name === "slim");
            pane.classList.toggle("is-expanded", name === "expanded");
            return pane.offsetHeight;
        };

        const heights = { slim: heightAs("slim"), default: heightAs("default"), expanded: heightAs("expanded") };
        pane.classList.remove("is-measuring");
        pane.classList.toggle("is-slim", slim);
        pane.classList.toggle("is-expanded", expanded);
        if (style === null) {
            pane.removeAttribute("style");
        } else {
            pane.setAttribute("style", style);
        }

        // The expanded probe made the body taller, and the browser cut its scroll to fit: it goes back where it was.
        if (body.scrollTop !== scrollTop) {
            body.scrollTop = scrollTop;
        }

        heights.expanded = Math.max(heights.default, heights.expanded);
        return heights;
    }

    function clearInline() {
        pane.style.removeProperty("height");
        pane.style.removeProperty("max-height");
        pane.style.removeProperty("transform");
        pane.style.removeProperty("transition");
    }

    // Holds the sheet at a fixed height, outside the classes' sizes, while it moves.
    function pin(height) {
        pane.style.transition = "none";
        pane.style.maxHeight = "none";
        pane.style.height = `${height}px`;
        pane.classList.remove("is-slim", "is-expanded");
        setSlimVar(false);
    }

    function setSlimVar(on) {
        const root = document.documentElement.style;
        if (on) {
            root.setProperty(SLIM_VAR, `${pane.offsetHeight}px`);
            slimSheet = pane;
        } else if (slimSheet === pane) {
            root.removeProperty(SLIM_VAR);
            slimSheet = null;
        }
    }

    function updateHandle() {
        handle?.setAttribute("aria-expanded", size() === "slim" ? "false" : "true");
    }

    function emit(reason) {
        lastHeight = pane.offsetHeight;
        document.dispatchEvent(new CustomEvent(PANE_CHANGE_EVENT, { detail: { reason } }));
    }

    // Above slim the height follows the finger; below it the sheet slides down whole.
    function place(height) {
        const { slim, expanded } = drag.heights;
        if (height >= slim) {
            pane.style.height = `${Math.min(height, expanded)}px`;
            pane.style.transform = "";
        } else {
            pane.style.height = `${slim}px`;
            pane.style.transform = `translateY(${slim - height}px)`;
        }
    }

    function animate(apply, done) {
        if (reducedMotion.matches) {
            apply();
            done();
            return;
        }

        pane.style.transition = `height ${SNAP_MS}ms ease-out, transform ${SNAP_MS}ms ease-out`;
        void pane.offsetHeight; // start the transition from where the sheet is
        apply();
        clearTimeout(settleTimer);
        settleTimer = setTimeout(done, SNAP_MS);
    }

    // Stops a size change where it is: the sheet stays at the height and offset it had reached.
    function freeze() {
        if (!settleTimer || dismissing) {
            return;
        }

        clearTimeout(settleTimer);
        settleTimer = 0;
        const { height, transform } = getComputedStyle(pane);
        pane.style.transition = "none";
        pane.style.height = height;
        pane.style.transform = transform === "none" ? "" : transform;
        state = size();
        target = null;
    }

    function transitionTo(next) {
        if (dismissing || wide.matches) {
            return;
        }

        freeze();
        const heights = measure();
        if (next === "slim" && body.contains(document.activeElement)) {
            // The body is about to be hidden: the focus stays on the sheet, on its bar, which brings it back.
            handle?.focus({ preventScroll: true });
        }

        // The body shows while the sheet grows or shrinks; the classes come back once it's there.
        pin(pane.offsetHeight);
        target = next;
        updateHandle();
        animate(
            () => {
                pane.style.height = `${heights[next]}px`;
                pane.style.transform = "";
            },
            () => finish(next));
    }

    // Back to CSS, so the size follows the content again (prices loading, a station removed…).
    function finish(next) {
        settleTimer = 0;
        target = null;
        state = next;
        pane.classList.toggle("is-slim", next === "slim");
        pane.classList.toggle("is-expanded", next === "expanded");
        clearInline();
        setSlimVar(next === "slim");
        updateHandle();
        emit("resize");
    }

    function replayEntrance() {
        pane.style.animation = "none";
        void pane.offsetHeight;
        pane.style.removeProperty("animation");
    }

    function dismiss() {
        dismissing = true;
        animate(
            () => {
                pane.style.transform = "translateY(100%)";
            },
            () => {
                settleTimer = 0;
                dotnet.invokeMethodAsync("OnSheetDismissed").catch(e => console.error(e));
                // Normally the pane is gone by now; if closing didn't happen, don't leave it off screen.
                stuckTimer = setTimeout(() => {
                    stuckTimer = 0;
                    if (pane.isConnected && dismissing) {
                        dismissing = false;
                        finish("slim");
                        replayEntrance();
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

    // Where a release at `height` goes: thrown, to the next size that way; else to the nearest. Only a sheet that was slim
    // closes, so the trip or the station shown aren't lost in one gesture.
    function snapTarget(height, speed, startSize, heights) {
        if (startSize === "slim"
            && (height < heights.slim * (1 - DISMISS_SHARE) || (speed > FLICK_PX_PER_MS && height <= heights.slim + 1))) {
            return "closed";
        }

        const sizes = [["slim", heights.slim], ["default", heights.default]];
        if (heights.expanded > heights.default + 1) {
            sizes.push(["expanded", heights.expanded]);
        }

        if (speed > FLICK_PX_PER_MS) {
            return sizes.findLast(([, h]) => h < height - 1)?.[0] ?? "slim";
        }

        if (speed < -FLICK_PX_PER_MS) {
            return (sizes.find(([, h]) => h > height + 1) ?? sizes[sizes.length - 1])[0];
        }

        return sizes.reduce((a, b) => Math.abs(b[1] - height) < Math.abs(a[1] - height) ? b : a)[0];
    }

    function onPointerDown(e) {
        tap = null;
        // A second finger doesn't take over the drag of the first.
        if (wide.matches || e.button !== 0 || dismissing || drag?.moved || !e.target.closest("header, .handle")
            || e.target.closest(CONTROLS)) {
            return;
        }

        drag = {
            id: e.pointerId,
            downY: e.clientY,
            farthest: 0, // the farthest the press went from where it started: back where it started, it's no tap
            startY: e.clientY,
            startHeight: 0,
            startSize: size(),
            onHandle: !!e.target.closest(".handle"),
            moved: false,
            heights: null,
            samples: [{ t: e.timeStamp, y: e.clientY }],
        };

        // A finger is captured by the element it touched, a mouse or pen isn't: this keeps the moves and the release coming
        // here once the pointer is over the map. A tap's click then goes to the pane, which reads what was pressed from tap.
        if (e.pointerType !== "touch") {
            pane.setPointerCapture(e.pointerId);
        }
    }

    function onPointerMove(e) {
        if (!drag || e.pointerId !== drag.id) {
            return;
        }

        // The button was released where the pane didn't hear it.
        if (!(e.buttons & 1)) {
            onPointerUp(new PointerEvent("pointercancel", { pointerId: e.pointerId }));
            return;
        }

        drag.farthest = Math.max(drag.farthest, Math.abs(e.clientY - drag.downY));
        if (!drag.moved) {
            if (drag.farthest < DRAG_SLOP_PX) {
                return;
            }

            drag.moved = true;
            freeze();
            drag.heights = measure();
            drag.startHeight = pane.offsetHeight;
            drag.startY = e.clientY;
            pin(drag.startHeight);
            // A finger now too, rather than the element it touched, which the page may replace while it drags.
            if (!pane.hasPointerCapture(e.pointerId)) {
                pane.setPointerCapture(e.pointerId);
            }
        }

        drag.samples.push({ t: e.timeStamp, y: e.clientY });
        if (drag.samples.length > 20) {
            drag.samples.shift();
        }

        place(drag.startHeight - (e.clientY - drag.startY));
    }

    function onPointerUp(e) {
        if (!drag || e.pointerId !== drag.id) {
            return;
        }

        const { moved, downY, farthest, startY, startHeight, startSize, onHandle, heights, samples } = drag;
        drag = null;

        // A cancelled pointer (the system took the gesture) has no meaningful position: settle where it last was.
        const cancelled = e.type === "pointercancel";
        if (!cancelled && Math.max(farthest, Math.abs(e.clientY - downY)) < TAP_SLOP_PX) {
            tap = { handle: onHandle };
            if (moved) {
                transitionTo(startSize);
            }

            return;
        }

        if (!moved) {
            return;
        }

        const y = cancelled ? samples[samples.length - 1].y : e.clientY;
        const speed = cancelled ? 0 : velocity([...samples, { t: e.timeStamp, y }]);
        const next = snapTarget(startHeight - (y - startY), speed, startSize, heights);
        if (next === "closed") {
            dismiss();
        } else {
            transitionTo(next);
        }
    }

    // After a release the drag is over already; otherwise the pointer went elsewhere, and it ends as if cancelled. Only the
    // pane's own capture counts: the element a finger touched loses its capture to the pane when the drag starts, and that
    // event bubbles here too.
    function onLostCapture(e) {
        if (e.target === pane && drag && e.pointerId === drag.id) {
            onPointerUp(new PointerEvent("pointercancel", { pointerId: e.pointerId }));
        }
    }

    // A tap on the bar, or the bar's button from the keyboard (detail 0), switches slim and the default size; a tap on a
    // slim sheet's header, not on one of its controls, brings it back. A tap that closed a menu or bubble in the header
    // never gets here (essencePopup.js drops its click).
    function onClick(e) {
        const pressed = e.detail === 0 ? { handle: !!e.target.closest(".handle") } : tap;
        tap = null;
        if (!pressed || wide.matches || dismissing) {
            return;
        }

        if (pressed.handle) {
            transitionTo(size() === "slim" ? "default" : "slim");
        } else if (size() === "slim" && !e.target.closest(CONTROLS)) {
            transitionTo("default");
        }
    }

    function onWidthChange() {
        // Sliding away, it still closes.
        if (dismissing) {
            return;
        }

        clearTimeout(settleTimer);
        settleTimer = 0;
        target = null;
        if (drag) {
            if (pane.hasPointerCapture(drag.id)) {
                pane.releasePointerCapture(drag.id);
            }

            drag = null;
        }

        // The side panel has its own size, and a sheet starts again from its default one.
        state = "default";
        pane.classList.remove("is-slim", "is-expanded");
        clearInline();
        setSlimVar(false);
        updateHandle();
        emit("resize");
    }

    // The content changing the sheet's height (prices loading, a list growing), or the window the side panel's.
    const observer = new ResizeObserver(() => {
        if (busy() || pane.offsetHeight === lastHeight) {
            return;
        }

        if (state === "slim") {
            setSlimVar(true);
        }

        emit("resize");
    });

    // What the pane hides of the map, from its bottom: an expanded sheet only its default height, so expanding it (to read
    // or type) doesn't move the map.
    function cover() {
        return size() === "expanded" && !busy() ? Math.min(pane.offsetHeight, measure().default) : pane.offsetHeight;
    }

    function collapse() {
        if (!wide.matches && !dismissing && !drag?.moved && size() !== "slim") {
            transitionTo("slim");
        }
    }

    pane.addEventListener("pointerdown", onPointerDown);
    pane.addEventListener("pointermove", onPointerMove);
    pane.addEventListener("pointerup", onPointerUp);
    pane.addEventListener("pointercancel", onPointerUp);
    pane.addEventListener("lostpointercapture", onLostCapture);
    pane.addEventListener("click", onClick);
    wide.addEventListener("change", onWidthChange);
    observer.observe(pane, { box: "border-box" });
    sheets.set(pane, { cover, collapse });
    updateHandle();
    emit("open");

    return {
        // A slim sheet back to its default size: a station was chosen while it was.
        restore() {
            if (!wide.matches && !dismissing && !drag?.moved && size() === "slim") {
                transitionTo("default");
            }
        },

        // The sheet at its default size at once, even one sliding away: the pane now shows something else.
        reset() {
            const wasDismissing = dismissing;
            clearTimeout(settleTimer);
            clearTimeout(stuckTimer);
            settleTimer = 0;
            stuckTimer = 0;
            dismissing = false;
            target = null;
            if (drag) {
                if (pane.hasPointerCapture(drag.id)) {
                    pane.releasePointerCapture(drag.id);
                }

                drag = null;
            }

            if (state === "default" && !wasDismissing && !pane.getAttribute("style")) {
                return;
            }

            finish("default");
            if (wasDismissing) {
                replayEntrance();
            }
        },

        dispose() {
            clearTimeout(settleTimer);
            clearTimeout(stuckTimer);
            observer.disconnect();
            pane.removeEventListener("pointerdown", onPointerDown);
            pane.removeEventListener("pointermove", onPointerMove);
            pane.removeEventListener("pointerup", onPointerUp);
            pane.removeEventListener("pointercancel", onPointerUp);
            pane.removeEventListener("lostpointercapture", onLostCapture);
            pane.removeEventListener("click", onClick);
            wide.removeEventListener("change", onWidthChange);
            sheets.delete(pane);
            setSlimVar(false);
            // The pane may still be in the page while it's being removed: the map mustn't count it any more.
            pane.removeAttribute("data-ess-pane");
            emit("close");
        },
    };
}

// How much of the map the panes cover, in pixels: from its bottom for a sheet, from its right edge for the side panel.
// Measured from the layout, so an entrance animation or a sheet being dragged doesn't count until it's in place.
export function paneCover() {
    let bottom = 0;
    let right = 0;
    for (const pane of document.querySelectorAll("[data-ess-pane]")) {
        if (wide.matches) {
            right = Math.max(right, (pane.offsetParent?.clientWidth ?? 0) - pane.offsetLeft);
        } else {
            bottom = Math.max(bottom, sheets.get(pane)?.cover() ?? pane.offsetHeight);
        }
    }

    return { bottom, right };
}

// A tap on the map, on phones: the sheets open shrink to slim (one already is stays). False on wide screens, or with no
// sheet, when the tap is left to close what's open.
export function collapseSheets() {
    if (wide.matches || sheets.size === 0) {
        return false;
    }

    for (const { collapse } of sheets.values()) {
        collapse();
    }

    return true;
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
