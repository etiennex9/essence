// The car menu and the info bubble in Best near you's header (BestPanel.razor). Blazor renders them and decides which one
// is open; this module places it, moves the focus in the menu, and closes it on Escape, a tap outside or the focus leaving,
// telling .NET (OnPopupClosed). One popup is open at a time. It listens on the window in the capture phase, so it sees
// Escape and a tap before the map does (essenceMap.js), which would close the whole pane: they close only the popup.

const EDGE_PX = 8; // kept from the screen's edges
const GAP_PX = 6; // between the popup and its trigger (BestPanel.razor.css)
const SWALLOW_MS = 1000; // a tap outside closes the popup, and the click it ends with is dropped if it comes this soon

let current = null; // { trigger, panel, dotnet, menu }
let swallowTimer = 0;

function menuItems() {
    return [...current.panel.querySelectorAll('[role^="menuitem"]')];
}

// Below the trigger, or above it when there's no room below and more above (a short bottom sheet), no taller than the
// room on that side (--popup-room: the rest scrolls); moved in from the left edge when it would go past it (it's aligned
// with the trigger's right edge).
function place(trigger, panel) {
    delete panel.dataset.side;
    panel.style.removeProperty("right");
    panel.style.removeProperty("--popup-room");
    const viewport = window.visualViewport;
    const top = viewport ? viewport.offsetTop : 0;
    const bottom = viewport ? viewport.offsetTop + viewport.height : window.innerHeight;
    const box = trigger.getBoundingClientRect();
    const above = box.top - top - GAP_PX - EDGE_PX;
    const below = bottom - box.bottom - GAP_PX - EDGE_PX;
    const flip = below < panel.offsetHeight && above > below;
    if (flip) {
        panel.dataset.side = "above";
    }

    panel.style.setProperty("--popup-room", `${Math.max(0, flip ? above : below)}px`);

    const left = panel.getBoundingClientRect().left;
    if (left < EDGE_PX) {
        panel.style.right = `${left - EDGE_PX}px`;
    }
}

function onKeyDown(e) {
    if (!current) {
        return;
    }

    if (e.key === "Escape") {
        e.preventDefault();
        e.stopPropagation();
        dismiss(true);
        return;
    }

    const focused = document.activeElement;
    if (!current.menu || !(current.panel.contains(focused) || focused === current.trigger)) {
        return;
    }

    if (e.key === "Tab") {
        // The focus moves on from the menu as from its button.
        dismiss(false);
        return;
    }

    const items = menuItems();
    const index = items.indexOf(focused);
    let next;
    switch (e.key) {
        case "ArrowDown":
            next = (index + 1) % items.length;
            break;
        case "ArrowUp":
            next = index < 0 ? items.length - 1 : (index - 1 + items.length) % items.length;
            break;
        case "Home":
            next = 0;
            break;
        case "End":
            next = items.length - 1;
            break;
        default:
            return;
    }

    e.preventDefault();
    items[next]?.focus();
}

function onPointerDown(e) {
    if (!current || current.panel.contains(e.target) || current.trigger.contains(e.target)) {
        return;
    }

    swallowNextClick();
    dismiss(false);
}

// The click a tap outside ends with would otherwise reach what's under it: on the map, that closes the pane. A click from
// the keyboard (detail 0) isn't the tap's.
function swallowNextClick() {
    disarm();
    window.addEventListener("click", onClick, true);
    window.addEventListener("pointerdown", disarm, true);
    window.addEventListener("pointercancel", disarm, true);
    swallowTimer = setTimeout(disarm, SWALLOW_MS);
}

function onClick(e) {
    if (e.detail > 0) {
        e.preventDefault();
        e.stopPropagation();
    }

    disarm();
}

function disarm() {
    clearTimeout(swallowTimer);
    window.removeEventListener("click", onClick, true);
    window.removeEventListener("pointerdown", disarm, true);
    window.removeEventListener("pointercancel", disarm, true);
}

// Safari doesn't focus a button that's clicked, so a focus going nowhere (relatedTarget null) is left to the tap.
function onFocusOut(e) {
    const to = e.relatedTarget;
    if (current && to && !current.panel.contains(to) && !current.trigger.contains(to)) {
        dismiss(false);
    }
}

function dismiss(restoreFocus) {
    const dotnet = current?.dotnet;
    close(restoreFocus);
    dotnet?.invokeMethodAsync("OnPopupClosed").catch(e => console.error(e));
}

// Opens `panel`, the popup `trigger` shows. A menu's focus goes to its checked item ("checked", else the first), its
// first or last ("first", "last"), or stays on the trigger (null).
export function open(trigger, panel, dotnet, focus) {
    close(false);
    current = { trigger, panel, dotnet, menu: panel.getAttribute("role") === "menu" };
    place(trigger, panel);
    window.addEventListener("keydown", onKeyDown, true);
    window.addEventListener("pointerdown", onPointerDown, true);
    trigger.addEventListener("focusout", onFocusOut);
    panel.addEventListener("focusout", onFocusOut);
    if (current.menu && focus) {
        const items = menuItems();
        const target = focus === "last" ? items[items.length - 1]
            : focus === "checked" ? panel.querySelector('[aria-checked="true"]') ?? items[0]
            : items[0];
        target?.focus();
    }
}

// Closes the popup open, without telling .NET (which asked for it); with restoreFocus, the focus goes back to its trigger.
export function close(restoreFocus) {
    if (!current) {
        return;
    }

    const { trigger, panel } = current;
    current = null;
    window.removeEventListener("keydown", onKeyDown, true);
    window.removeEventListener("pointerdown", onPointerDown, true);
    trigger.removeEventListener("focusout", onFocusOut);
    panel.removeEventListener("focusout", onFocusOut);
    if (restoreFocus && trigger.isConnected) {
        trigger.focus();
    }
}
