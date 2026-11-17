// The MapLibre map behind MapInterop.cs. C# decides what to show (markers and a trip's route arrive preformatted, each
// marker with the zoom from which it has room for its price); this module draws it and reports what the user does: map
// moves, marker clicks, a press and hold on the map, a click on the map (which shrinks a phone's sheet, or closes what's
// open), Escape, and when the page is hidden or shown again. It keeps the part of the map a pane covers as the map's
// padding, so its center, and what the page centers or fits, are in the part left uncovered. It also reloads the page
// for a new release, keeping the view (UpdateCheck.cs): here
// rather than in a module of its own, which a tab would import from the new release only when it's time to reload.
import { Map as MapLibreMap, Marker, NavigationControl } from "../lib/maplibre-gl/maplibre-gl.mjs";
import { collapseSheets, paneCover, PANE_CHANGE_EVENT } from "./essencePane.js";

const VIEWPORT_DEBOUNCE_MS = 250;
const PANE_SETTLE_MS = 260;
const VISIBLE_MARGIN_PX = 48;

// A pane never counts as covering more of the map than its size less this: the part left is where things are centered.
const MIN_VISIBLE_PX = 160;

// fitBounds keeps the points this far inside the area left uncovered.
const FIT_PADDING_PX = 56;
const FIT_MAX_ZOOM = 15;

// Above them it keeps them under the toolbar, and what stands above a point clear of it: any more, and the map zooms out
// and sits low for nothing.
const FIT_TOP_PX = 64;
const FIT_FLAG_PX = 66; // a station's pill and flag
const FIT_LABEL_PX = 35;

// Beside a tall sheet on a short screen, the margin under the points gives way down to this, nothing standing below a
// point, until they have this much height.
const FIT_TIGHT_PX = 16;
const FIT_ROOM_PX = 240;

// Press and hold (a long press on Android, a touch timer on iOS, which has none, the left mouse button held, or a
// right-click) picks a destination. A finger or mouse that moves farther than this, or a second finger, is panning or
// zooming instead.
const HOLD_MS = 550;
const HOLD_SLOP_PX = 10;

// Android fires contextmenu around when the touch timer does: a second hold this soon after is the same one. The click
// that ends a hold (when the finger or the button lifts, however long after) mustn't count as a click on the map, which
// would shrink or close the pane.
const HOLD_REPEAT_MS = 800;
const HOLD_CLICK_MS = 700;

// A marker opens from its dot to its price at the zoom C# gives it (pillFrom, PillPlacement.cs), and folds back only this
// far below it: a pinch resting on that zoom doesn't make it blink.
const PILL_HYSTERESIS_ZOOM = 0.1;

// What a press and hold on doesn't drop a pin: a marker, or the map's own buttons.
const NOT_HELD = ".ess-marker, .maplibregl-ctrl";

// A trip's route (setRoute): GeoJSON sources and layers drawn over the roads, under the labels.
const ROUTE_SOURCE = "ess-route";
const BEYOND_SOURCE = "ess-route-beyond";
const CORRIDOR_SOURCE = "ess-corridor";
const ROUTE_LAYERS = ["ess-route-corridor", "ess-route-casing", "ess-route-line", "ess-route-beyond-casing", "ess-route-beyond"];

// The corridor shows on a trip seen whole, and fades out as the map zooms in on a stretch of it.
const CORRIDOR_FADE_FROM_ZOOM = 10;
const CORRIDOR_GONE_AT_ZOOM = 13;

// The view a reload for a new release saves, for the page it reloads into: the version before writes it and the version
// after reads it, so it's read leniently.
const RELOAD_KEY = "essence.reload";

const darkScheme = window.matchMedia("(prefers-color-scheme: dark)");

function ensureStylesheet() {
    if (document.querySelector("link[data-maplibre-css]")) {
        return;
    }

    const link = document.createElement("link");
    link.rel = "stylesheet";
    link.href = new URL("../lib/maplibre-gl/maplibre-gl.css", import.meta.url).href;
    link.dataset.maplibreCss = "";
    document.head.append(link);
}

// A generic pump for stations without a brand.
const PUMP_SVG = '<svg viewBox="0 0 24 24" width="12" height="12" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" ' +
    'stroke-linecap="round" stroke-linejoin="round"><path d="M3 22V5a2 2 0 0 1 2-2h7a2 2 0 0 1 2 2v17M2 22h13M6 9h5"/>' +
    '<path d="M14 11h2a2 2 0 0 1 2 2v4a1.5 1.5 0 0 0 3 0V8l-3-3"/></svg>';

// The favorite badge.
const STAR_SVG = '<svg viewBox="0 0 24 24" width="9" height="9" aria-hidden="true" fill="currentColor">' +
    '<path d="M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01L12 2z"/></svg>';

// The view saved by reload(), or null. Taken once, and only by the reload it was saved for, not by a later visit in the
// same tab. Storage can be blocked (private windows, site data turned off): then there's nothing to take.
export function takeSavedView() {
    let saved;
    try {
        saved = sessionStorage.getItem(RELOAD_KEY);
        sessionStorage.removeItem(RELOAD_KEY);
    } catch {
        return null;
    }

    if (!saved || performance.getEntriesByType("navigation")[0]?.type !== "reload") {
        return null;
    }

    let view;
    try {
        view = JSON.parse(saved);
    } catch {
        return null;
    }

    if (!view || ![view.lat, view.lon, view.zoom].every(Number.isFinite)) {
        return null;
    }

    const hasUser = Number.isFinite(view.userLat) && Number.isFinite(view.userLon);
    return {
        lat: view.lat,
        lon: view.lon,
        zoom: view.zoom,
        userLat: hasUser ? view.userLat : null,
        userLon: hasUser ? view.userLon : null,
        fuel: typeof view.fuel === "string" ? view.fuel : null,
        stationPushed: view.stationPushed === true,
        listPushed: view.listPushed === true,
        carsPushed: view.carsPushed === true,
        tripPushed: view.tripPushed === true,
        carsOverTrip: view.carsOverTrip === true,
        placesPushed: view.placesPushed === true,
        bestSelectedId: Number.isSafeInteger(view.bestSelectedId) ? view.bestSelectedId : null,
    };
}

function lineString(coordinates) {
    return { type: "Feature", properties: {}, geometry: { type: "LineString", coordinates: coordinates ?? [] } };
}

function multiPolygon(coordinates) {
    return { type: "Feature", properties: {}, geometry: { type: "MultiPolygon", coordinates: coordinates ?? [] } };
}

function cssColor(name, fallback) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

// A marker with a text label over a stem: the destination, or where the range runs out.
function labelElement(className, text) {
    const element = document.createElement("div");
    element.className = className;
    element.setAttribute("aria-hidden", "true");
    const label = document.createElement("b");
    label.textContent = text;
    element.append(label, document.createElement("i"));
    return element;
}

export function createMap(container, options, dotnet) {
    ensureStylesheet();
    document.documentElement.lang = options.language;

    const map = new MapLibreMap({
        container,
        style: darkScheme.matches ? options.darkStyleUrl : options.styleUrl,
        center: [options.centerLon, options.centerLat],
        zoom: options.zoom,
        attributionControl: { compact: true },
        dragRotate: false,
        pitchWithRotate: false,
        touchPitch: false,
    });
    map.touchZoomRotate.disableRotation();
    map.keyboard.disableRotation();
    map.addControl(new NavigationControl({ showCompass: false }), "bottom-right");

    const markers = new Map(); // id → { marker, element, key, z, pillFrom, pill }
    let selectedId = null;
    let userMarker = null;

    // The trip on the map: its route (null: none), destination, where the range runs out, and a dropped pin.
    let route = null;
    let destinationMarker = null;
    let rangeEndMarker = null;
    let pinMarker = null;

    // Press and hold.
    let holdTimer = 0;
    let holdStart = null;
    let heldThisPress = false;
    let lastHoldAt = 0;
    let swallowClickUntil = 0;

    // Best near you's pick and closest, with their flags' text, and the stations listed with them (null: none fade).
    let emphasis = { pickId: null, closestId: null, listed: null, bestFlag: "", closestFlag: "" };
    let viewportTimer = 0;

    // flyTo, fitBounds and ensureVisible calls waiting to start their move (see reload).
    let pendingMoves = 0;

    // The last of those moves, made again in the part left uncovered when a pane opens, closes or changes size, until the
    // user moves the map (null). A move asked for before the page showed something else (forgetAutoMove: another
    // generation) isn't kept.
    let lastAuto = null;
    let autoGeneration = 0;

    // The padding follows the panes at the next frame, or once a finger or the mouse's button that's down is lifted.
    let syncFrame = 0;
    let syncDeferred = false;
    let fingersDown = 0;
    let mouseDown = false;
    let userMoving = false; // the map moves for a gesture of the user's, or glides after one

    const call = (method, ...args) => dotnet.invokeMethodAsync(method, ...args).catch(e => console.error(e));

    function reportViewport() {
        clearTimeout(viewportTimer);
        viewportTimer = setTimeout(() => {
            const bounds = map.getBounds();
            const center = map.getCenter();
            call("OnViewportChanged", {
                centerLat: center.lat,
                centerLon: center.lng,
                west: bounds.getWest(),
                south: bounds.getSouth(),
                east: bounds.getEast(),
                north: bounds.getNorth(),
                zoom: map.getZoom(),
            });
        }, VIEWPORT_DEBOUNCE_MS);
    }

    // Each marker shows its price from its pillFrom zoom up, and is a brand dot below; one showing its price keeps it down to
    // PILL_HYSTERESIS_ZOOM under that.
    function updatePills() {
        const zoom = map.getZoom();
        for (const entry of markers.values()) {
            const pill = zoom >= entry.pillFrom - (entry.pill ? PILL_HYSTERESIS_ZOOM : 0);
            if (pill !== entry.pill) {
                entry.pill = pill;
                entry.element.classList.toggle("is-compact", !pill);
            }
        }
    }

    function onMapClick(e) {
        if (Date.now() < swallowClickUntil) {
            swallowClickUntil = 0;
            return;
        }

        if (!e.originalEvent.target.closest?.(".ess-marker")) {
            call("OnMapClicked");
        }
    }

    // A press and hold at a point of the container (pixels): C# drops a pin there.
    function held(point) {
        const now = Date.now();
        if (now - lastHoldAt < HOLD_REPEAT_MS) {
            return;
        }

        lastHoldAt = now;
        heldThisPress = true;
        lastAuto = null;
        swallowClickUntil = now + HOLD_CLICK_MS;
        const { lng, lat } = map.unproject(point);
        call("OnMapHeld", lat, lng);
    }

    function onContextMenu(e) {
        if (e.originalEvent.target.closest?.(NOT_HELD)) {
            return;
        }

        e.originalEvent.preventDefault();
        held(e.point);
    }

    function cancelHold() {
        clearTimeout(holdTimer);
        holdTimer = 0;
    }

    // A finger or the mouse's button down at a point of the page: held still long enough, it's a press and hold.
    function startHold(clientX, clientY, target) {
        cancelHold();
        heldThisPress = false;
        if (target.closest?.(NOT_HELD)) {
            return;
        }

        const rect = container.getBoundingClientRect();
        holdStart = { x: clientX, y: clientY, point: [clientX - rect.left, clientY - rect.top] };
        holdTimer = setTimeout(() => {
            holdTimer = 0;
            held(holdStart.point);
        }, HOLD_MS);
    }

    function moveHold(clientX, clientY) {
        if (holdTimer && Math.hypot(clientX - holdStart.x, clientY - holdStart.y) > HOLD_SLOP_PX) {
            cancelHold();
        }
    }

    function endHold() {
        cancelHold();
        if (heldThisPress) {
            heldThisPress = false;
            swallowClickUntil = Date.now() + HOLD_CLICK_MS;
        }
    }

    function onTouchStart(e) {
        if (e.touches.length !== 1) {
            cancelHold();
            heldThisPress = false;
            return;
        }

        startHold(e.touches[0].clientX, e.touches[0].clientY, e.target);
    }

    // The fingers on the map, not on a pane or the toolbar: the padding waits for them to lift. Counted from the document, as
    // a finger lifted off the map may end elsewhere; one whose element is gone from the page no longer counts.
    function countFingers(e) {
        fingersDown = [...e.touches].filter(t => container.contains(t.target)).length;
        if (fingersDown === 0) {
            flushDeferred();
        }
    }

    function onTouchMove(e) {
        if (e.touches.length !== 1) {
            cancelHold();
            return;
        }

        moveHold(e.touches[0].clientX, e.touches[0].clientY);
    }

    // The mouse's left button; fingers have the touch events above, and pens their own press and hold (a right-click).
    function onPointerDown(e) {
        if (e.pointerType !== "mouse") {
            return;
        }

        mouseDown = true;
        if (e.button === 0) {
            startHold(e.clientX, e.clientY, e.target);
        }
    }

    function onPointerMove(e) {
        if (e.pointerType === "mouse") {
            moveHold(e.clientX, e.clientY);
        }
    }

    function onPointerUp(e) {
        if (e.pointerType === "mouse") {
            endHold();
        }
    }

    // On the window: the button may be lifted anywhere.
    function onWindowPointerUp(e) {
        if (e.pointerType === "mouse" && mouseDown) {
            mouseDown = false;
            flushDeferred();
        }
    }

    // A gesture of the user's: the page's last move isn't made again.
    function onMoveStart(e) {
        cancelHold();
        if (e.originalEvent) {
            lastAuto = null;
            userMoving = true;
        }
    }

    function onMoveEnd() {
        userMoving = false;
        flushDeferred();
    }

    const userGesture = () => fingersDown > 0 || mouseDown || userMoving;

    function forgetAutoMove() {
        lastAuto = null;
    }

    function removeRouteLayers() {
        for (const id of ROUTE_LAYERS) {
            if (map.getLayer(id)) {
                map.removeLayer(id);
            }
        }

        for (const id of [ROUTE_SOURCE, BEYOND_SOURCE, CORRIDOR_SOURCE]) {
            if (map.getSource(id)) {
                map.removeSource(id);
            }
        }
    }

    // The route under the style's labels, in the theme's accent; past the range, dashed grey. A new style (the dark mode
    // switching) drops custom layers, so they're added again on each style.load; while a style is loading, adding them
    // throws, and style.load adds them once it's in.
    function addRouteLayers() {
        try {
            removeRouteLayers();
            if (route) {
                addRouteLayersNow();
            }
        } catch {
            // Not loaded yet.
        }
    }

    function addRouteLayersNow() {
        // Over every road and area, under the labels drawn after them: the dark style has labels before its roads.
        const layers = map.getStyle().layers;
        const lastShape = layers.findLastIndex(l => l.type !== "symbol");
        const before = layers.slice(lastShape + 1).find(l => l.type === "symbol")?.id;
        const accent = cssColor("--ess-accent", "#185fa5");
        const casing = cssColor("--ess-surface", "#ffffff");
        const round = { "line-cap": "round", "line-join": "round" };
        map.addSource(ROUTE_SOURCE, { type: "geojson", data: lineString(route.path) });
        map.addSource(BEYOND_SOURCE, { type: "geojson", data: lineString(route.beyond) });
        map.addSource(CORRIDOR_SOURCE, { type: "geojson", data: multiPolygon(route.corridor) });
        // One shape, filled once: where the route comes back near itself, the tint doesn't double.
        map.addLayer({
            id: "ess-route-corridor", type: "fill", source: CORRIDOR_SOURCE,
            paint: {
                "fill-color": accent,
                // Zoomed in on a short trip, the corridor would tint the whole map: it fades out by then.
                "fill-opacity": ["interpolate", ["linear"], ["zoom"], CORRIDOR_FADE_FROM_ZOOM, 0.12, CORRIDOR_GONE_AT_ZOOM, 0],
            },
        }, before);
        map.addLayer({ id: "ess-route-casing", type: "line", source: ROUTE_SOURCE, layout: round, paint: { "line-color": casing, "line-width": 8 } }, before);
        map.addLayer({ id: "ess-route-line", type: "line", source: ROUTE_SOURCE, layout: round, paint: { "line-color": accent, "line-width": 5 } }, before);
        map.addLayer({
            id: "ess-route-beyond-casing", type: "line", source: BEYOND_SOURCE, layout: round,
            paint: { "line-color": casing, "line-width": 8 },
        }, before);
        map.addLayer({
            id: "ess-route-beyond", type: "line", source: BEYOND_SOURCE, layout: { "line-join": "round" },
            paint: { "line-color": "#9a978e", "line-width": 5, "line-dasharray": [1.4, 1] },
        }, before);
    }


    // Shows, moves or (lat null) removes one of the trip's markers.
    function placeMarker(existing, lat, lon, create) {
        if (lat === null || lat === undefined) {
            existing?.remove();
            return null;
        }

        const marker = existing ?? create();
        return marker.setLngLat([lon, lat]).addTo(map);
    }

    function onKeyDown(e) {
        // Esc in a modal dialog (About) closes that dialog only, not the pane behind it; in the address search with text
        // typed, it clears the text (PlacesPanel), and the next one closes the pane; in a field that handles it itself
        // (renaming a place), it's that field's. An open menu or info bubble in the pane's header takes it first:
        // essencePopup.js stops it at the window, so it never gets here.
        if (e.key === "Escape" && !document.querySelector("dialog:modal") && !(e.target.matches?.("input[type=search]") && e.target.value)
            && !e.target.matches?.("[data-ess-own-escape]")) {
            call("OnDismiss");
        }
    }

    function onSchemeChange(e) {
        map.setStyle(e.matches ? options.darkStyleUrl : options.styleUrl);
    }

    // Another tab, or the app in the background: the prices needn't refresh until the map is seen again.
    function onVisibilityChange() {
        call("OnVisibilityChanged", document.visibilityState === "visible");
    }

    // The map's padding: the part the panes cover (essencePane.js), which its center, flyTo, fitBounds and easeTo keep
    // clear of. Never all of it.
    function paddingNow() {
        const { bottom, right } = paneCover();
        const { clientWidth: width, clientHeight: height } = container;
        return {
            top: 0,
            left: 0,
            right: Math.round(Math.max(0, Math.min(right, width - MIN_VISIBLE_PX))),
            bottom: Math.round(Math.max(0, Math.min(bottom, height - MIN_VISIBLE_PX))),
        };
    }

    // Sets the padding with nothing moving on screen: the center becomes the point in the middle of the new uncovered part.
    // False when it's unchanged.
    function applyPadding(padding) {
        const current = map.getPadding();
        if (["top", "right", "bottom", "left"].every(side => current[side] === padding[side])) {
            return false;
        }

        const { clientWidth: width, clientHeight: height } = container;
        const center = map.unproject([(padding.left + width - padding.right) / 2, (padding.top + height - padding.bottom) / 2]);
        map.jumpTo({ padding, center });
        return true;
    }

    // MapLibre catches up with its container's size (a rotation, the window crossing the wide breakpoint) at its own resize
    // event, which may come after the pane's: the padding is measured against the size the map is about to have. Its
    // canvas has the size it last took, to a device pixel.
    function fitTransform() {
        const canvas = map.getCanvas();
        const width = Math.floor(container.clientWidth);
        const height = Math.floor(container.clientHeight);
        if (width > 0 && height > 0 && (Math.abs(canvas.clientWidth - width) > 1 || Math.abs(canvas.clientHeight - height) > 1)) {
            map.resize();
        }
    }

    function requestSync() {
        if (!syncFrame) {
            syncFrame = requestAnimationFrame(syncPadding);
        }
    }

    // A pane opened, closed or changed size: the padding follows, and the page's last move is made again in the new
    // uncovered part, unless the user has moved the map since. Not under a finger or the mouse's button, whose gesture a
    // jump would end, nor while the map glides after one: once it's over.
    function syncPadding() {
        syncFrame = 0;
        if (userGesture() || (map.isMoving() && !lastAuto)) {
            syncDeferred = true;
            return;
        }

        syncDeferred = false;
        fitTransform();
        if (applyPadding(paddingNow()) && lastAuto && pendingMoves === 0) {
            run(lastAuto, true);
        }
    }

    function flushDeferred() {
        if (syncDeferred) {
            requestSync();
        }
    }

    // A move made for the page; made again (replay), it eases rather than flies, being short. False if there was no need.
    function run(move, replay = false) {
        switch (move.kind) {
            case "fly":
                if (replay) {
                    map.easeTo({ center: [move.lon, move.lat], zoom: move.zoom });
                } else {
                    map.flyTo({ center: [move.lon, move.lat], zoom: move.zoom });
                }

                break;
            case "fit":
                fit(move, replay);
                break;
            case "visible":
                return showPoint(move);
        }

        return true;
    }

    // fitBounds keeps the box inside the uncovered part: MapLibre adds the map's padding to these margins, which give way
    // where that part is short, so the box never ends up under the pane.
    function fit(box, linear) {
        const { west, south, east, north } = box;
        const pane = map.getPadding();
        const { clientWidth: width, clientHeight: height } = container;
        const room = height - pane.top - pane.bottom;
        const bottom = Math.min(FIT_PADDING_PX, Math.max(FIT_TIGHT_PX, room - FIT_TOP_PX - FIT_FLAG_PX - FIT_ROOM_PX));
        const top = Math.min(fitTop(box, room - bottom), room - bottom - FIT_PADDING_PX);
        const side = Math.min(FIT_PADDING_PX, (width - pane.left - pane.right - FIT_PADDING_PX) / 2);
        const padding = { top, left: side, right: side, bottom };
        map.fitBounds([[west, south], [east, north]], { padding, maxZoom: FIT_MAX_ZOOM, linear });
    }

    // The margin above the box that keeps what stands on its points under the toolbar. A point at a share of the box's
    // height, with the box from `top` down to `reach`, ends up at top + (reach - top) * share: the lower, the less it needs.
    function fitTop({ west, south, north, flags, labels }, reach) {
        const y = lat => map.project([west, lat]).y;
        const height = y(south) - y(north);
        const margin = (lat, standing) => {
            const share = height > 0 ? (y(lat) - y(north)) / height : 0;
            return share < 1 ? (FIT_TOP_PX + standing - reach * share) / (1 - share) : 0;
        };

        return Math.max(FIT_TOP_PX, ...flags.map(lat => margin(lat, FIT_FLAG_PX)), ...labels.map(lat => margin(lat, FIT_LABEL_PX)));
    }

    // Whether the point shows clear of the part `padding` covers.
    function inView({ lat, lon }, { top, right, bottom, left }) {
        const { clientWidth: width, clientHeight: height } = container;
        const point = map.project([lon, lat]);
        return point.x >= left + VISIBLE_MARGIN_PX && point.x <= width - right - VISIBLE_MARGIN_PX
            && point.y >= top + VISIBLE_MARGIN_PX && point.y <= height - bottom - VISIBLE_MARGIN_PX;
    }

    function inBox({ lat, lon }, { west, south, east, north }) {
        return lat >= south && lat <= north && lon >= west && lon <= east;
    }

    // Pans just enough that the point isn't under a pane: to the middle of the uncovered part, if it's outside it.
    function showPoint(move) {
        if (inView(move, map.getPadding())) {
            return false;
        }

        map.easeTo({ center: [move.lon, move.lat] });
        return true;
    }

    // Makes `move` once a pane opening is in place (wait), with the padding it then leaves. It's the move made again when a
    // pane changes size, unless the page showed something else in the meantime. A point inside the box last fitted is shown
    // by fitting that again, and a point already in view leaves what framed it: a station chosen on the way keeps the route
    // in view, even as its pane comes back from slim.
    function schedule(move, wait) {
        pendingMoves++;
        const generation = autoGeneration;
        setTimeout(() => {
            pendingMoves--;
            fitTransform();
            const padding = paddingNow();
            const target = move.kind === "visible" && lastAuto?.kind === "fit" && inBox(move, lastAuto) ? lastAuto : move;
            let moved = false;
            if (move.kind === "visible" && userGesture() && inView(move, padding)) {
                // In view already, while the user moves the map: that goes on, and the padding follows once it's over.
                syncDeferred = true;
            } else {
                // Any other move ends the user's.
                applyPadding(padding);
                moved = run(target, target !== move);
            }

            // Not one the user's gesture went on after.
            if (generation === autoGeneration && !userMoving && (moved || !lastAuto)) {
                lastAuto = target;
            }
        }, wait ? PANE_SETTLE_MS : 0);
    }

    function applyEmphasis(element, id) {
        const best = id === emphasis.pickId;
        const closest = !best && id === emphasis.closestId;
        element.classList.toggle("is-best", best);
        element.classList.toggle("is-closest", closest);
        element.classList.toggle("is-dimmed", emphasis.listed !== null && !emphasis.listed.has(id) && id !== selectedId);
        if (best || closest) {
            element.dataset.flag = best ? emphasis.bestFlag : emphasis.closestFlag;
        } else {
            delete element.dataset.flag;
        }
    }

    function renderMarker(element, model) {
        element.dataset.tone = model.tone;
        element.classList.toggle("is-unavailable", model.unavailable);
        element.classList.toggle("is-closed", model.closed);
        element.classList.toggle("is-favorite", model.favorite);
        element.classList.toggle("is-selected", model.id === selectedId);
        applyEmphasis(element, model.id);
        element.setAttribute("aria-label", model.label);
        element.title = model.label;

        const logo = document.createElement("span");
        logo.className = "ess-logo";
        logo.style.setProperty("--logo-bg", model.background);
        logo.style.setProperty("--logo-fg", model.foreground);
        if (model.logoUrl) {
            const img = document.createElement("img");
            img.src = model.logoUrl;
            img.alt = "";
            logo.classList.add("has-image");
            logo.append(img);
        } else if (model.monogram) {
            logo.textContent = model.monogram;
            logo.classList.toggle("is-wide", model.monogram.length > 1);
        } else {
            logo.innerHTML = PUMP_SVG;
        }

        const price = document.createElement("span");
        price.className = "ess-price";
        price.textContent = model.price;

        const pill = document.createElement("span");
        pill.className = "ess-pill";
        pill.append(logo, price);
        if (model.favorite) {
            const star = document.createElement("span");
            star.className = "ess-fav";
            star.innerHTML = STAR_SVG;
            pill.append(star);
        }

        element.replaceChildren(pill);
    }

    map.on("load", reportViewport);
    map.on("moveend", reportViewport);
    map.on("zoom", updatePills);
    map.on("click", onMapClick);
    map.on("contextmenu", onContextMenu);
    map.on("movestart", onMoveStart);
    map.on("boxzoomstart", forgetAutoMove);
    map.on("moveend", onMoveEnd);
    map.on("resize", requestSync);
    map.on("style.load", addRouteLayers);
    container.addEventListener("touchstart", onTouchStart, { passive: true });
    container.addEventListener("touchmove", onTouchMove, { passive: true });
    container.addEventListener("touchend", endHold);
    container.addEventListener("touchcancel", endHold);
    container.addEventListener("pointerdown", onPointerDown);
    container.addEventListener("pointermove", onPointerMove);
    container.addEventListener("pointerup", onPointerUp);
    container.addEventListener("pointercancel", onPointerUp);
    window.addEventListener("pointerup", onWindowPointerUp, true);
    window.addEventListener("pointercancel", onWindowPointerUp, true);
    for (const type of ["touchstart", "touchend", "touchcancel"]) {
        document.addEventListener(type, countFingers, { capture: true, passive: true });
    }

    document.addEventListener(PANE_CHANGE_EVENT, requestSync);
    // Captured, so it sees the search box's text before Blazor's own handler (which may be registered first) clears it.
    document.addEventListener("keydown", onKeyDown, true);
    darkScheme.addEventListener("change", onSchemeChange);
    document.addEventListener("visibilitychange", onVisibilityChange);
    // A pane in the page already (a link to a station or a list): the map starts clear of it, attached or not yet.
    applyPadding(paddingNow());
    if (document.visibilityState !== "visible") {
        // Opened in a background tab: no event comes until it's shown.
        onVisibilityChange();
    }

    return {
        // Adds, updates and removes markers so the map shows exactly `models`. Unchanged markers aren't touched, nor are those
        // whose drawing order or zoom for their price is all that changed, beyond that.
        setMarkers(models) {
            const keep = new Set();
            for (const model of models) {
                keep.add(model.id);
                let entry = markers.get(model.id);
                if (!entry) {
                    const element = document.createElement("button");
                    element.type = "button";
                    element.className = "ess-marker";
                    element.addEventListener("click", e => {
                        e.stopPropagation();
                        call("OnMarkerClicked", model.id);
                    });
                    const marker = new Marker({ element, anchor: "bottom" }).setLngLat([model.lon, model.lat]).addTo(map);
                    entry = { marker, element, key: "", z: null, pillFrom: 0, pill: null };
                    markers.set(model.id, entry);
                }

                const { z, pillFrom, ...drawn } = model;
                const key = JSON.stringify(drawn);
                if (entry.key !== key) {
                    renderMarker(entry.element, drawn);
                    entry.marker.setLngLat([model.lon, model.lat]);
                    entry.key = key;
                }

                if (entry.z !== z) {
                    entry.element.style.zIndex = String(z + 1);
                    entry.z = z;
                }

                entry.pillFrom = pillFrom;
            }

            for (const [id, entry] of markers) {
                if (!keep.has(id)) {
                    entry.marker.remove();
                    markers.delete(id);
                }
            }

            // Before the new markers are painted: a crowded set of prices never shows.
            updatePills();
        },

        setSelected(id) {
            selectedId = id ?? null;
            for (const [markerId, entry] of markers) {
                entry.element.classList.toggle("is-selected", markerId === selectedId);
                applyEmphasis(entry.element, markerId);
            }
        },

        // Best near you: the pick and the closest stand out, flagged with bestFlag and closestFlag; with listedIds, the
        // markers not in it fade. Null ids clear it.
        setEmphasis(pickId, closestId, listedIds, bestFlag, closestFlag) {
            const listed = listedIds ? new Set(listedIds) : null;
            emphasis = { pickId: pickId ?? null, closestId: closestId ?? null, listed, bestFlag, closestFlag };
            for (const [id, entry] of markers) {
                applyEmphasis(entry.element, id);
            }
        },

        // A trip's route, [lon, lat] pairs: the part the car reaches, and the part past its range (dashed; null when none),
        // with the corridor stations were looked for in (GeoJSON MultiPolygon coordinates, TripCorridor).
        setRoute(path, beyond, corridor) {
            route = { path, beyond, corridor };
            addRouteLayers();
        },

        clearRoute() {
            route = null;
            addRouteLayers();
        },

        // The trip's destination, labelled; a null lat removes it.
        setDestination(lat, lon, label) {
            destinationMarker?.remove();
            destinationMarker = placeMarker(null, lat, lon,
                () => new Marker({ element: labelElement("ess-dest", label), anchor: "bottom" }));
        },

        // Where the range runs out along the route, labelled; a null lat removes it.
        setRangeEnd(lat, lon, label) {
            rangeEndMarker?.remove();
            rangeEndMarker = placeMarker(null, lat, lon,
                () => new Marker({ element: labelElement("ess-range-end", label), anchor: "bottom" }));
        },

        // The pin a press and hold dropped; a null lat removes it.
        setPin(lat, lon) {
            pinMarker = placeMarker(pinMarker, lat, lon, () => {
                const element = document.createElement("div");
                element.className = "ess-pin";
                element.setAttribute("aria-hidden", "true");
                return new Marker({ element, anchor: "bottom" });
            });
        },

        showUserLocation(lat, lon) {
            if (!userMarker) {
                const element = document.createElement("div");
                element.className = "ess-user";
                element.setAttribute("aria-hidden", "true");
                userMarker = new Marker({ element }).setLngLat([lon, lat]).addTo(map);
            } else {
                userMarker.setLngLat([lon, lat]);
            }
        },

        // Centers the point in the part of the map the panes leave uncovered; with waitForPane, once a pane opening is in place.
        flyTo(lat, lon, zoom, waitForPane) {
            schedule({ kind: "fly", lat, lon, zoom }, waitForPane);
        },

        // Shows the box (degrees) in the part the panes leave uncovered; with waitForPane, once a pane opening is in place.
        // Flags and labels are the latitudes in the box where one stands above its point.
        fitBounds(west, south, east, north, waitForPane, flags, labels) {
            schedule({ kind: "fit", west, south, east, north, flags, labels }, waitForPane);
        },

        // Pans just enough that the point isn't under a pane: once it's in place, unless it already is (waitForPane false,
        // for a station chosen in the pane open).
        ensureVisible(lat, lon, waitForPane = true) {
            schedule({ kind: "visible", lat, lon }, waitForPane);
        },

        // A tap on the map, on phones: the sheet open shrinks to its header. False with none, or on a wide screen: the tap
        // closes what's open.
        collapsePane() {
            return collapseSheets();
        },

        // The page shows something else (another station, list or trip): the last move isn't made again for it.
        forgetAutoMove() {
            autoGeneration++;
            lastAuto = null;
        },

        // Reloads the page. With saveView, the page it reloads into opens on this view, with the page's own `state` (C#'s
        // ReloadState) and the user's dot. An automatic reload that can't save it doesn't happen: the page keeps offering
        // it. The view is read here because C#'s is only reported once the map has drawn, which a page that opened out of
        // sight hasn't. A move on its way isn't saved halfway: out of sight, a flight to the user or a station doesn't
        // run at all (no animation frames), and the reload starts over instead.
        reload(state, saveView, automatic) {
            if (saveView && pendingMoves === 0 && !map.isMoving()) {
                // The middle of the whole map, not getCenter's of the uncovered part: the page reloaded starts without
                // padding, and keeps the view when it makes room for its pane.
                const center = map.unproject([container.clientWidth / 2, container.clientHeight / 2]);
                const user = userMarker?.getLngLat();
                const view = { ...state, lat: center.lat, lon: center.lng, zoom: map.getZoom(), userLat: user?.lat, userLon: user?.lng };
                try {
                    sessionStorage.setItem(RELOAD_KEY, JSON.stringify(view));
                } catch {
                    if (automatic) {
                        return;
                    }
                }
            }

            location.reload();
        },

        dispose() {
            clearTimeout(viewportTimer);
            cancelAnimationFrame(syncFrame);
            cancelHold();
            container.removeEventListener("touchstart", onTouchStart);
            container.removeEventListener("touchmove", onTouchMove);
            container.removeEventListener("touchend", endHold);
            container.removeEventListener("touchcancel", endHold);
            container.removeEventListener("pointerdown", onPointerDown);
            container.removeEventListener("pointermove", onPointerMove);
            container.removeEventListener("pointerup", onPointerUp);
            container.removeEventListener("pointercancel", onPointerUp);
            window.removeEventListener("pointerup", onWindowPointerUp, true);
            window.removeEventListener("pointercancel", onWindowPointerUp, true);
            for (const type of ["touchstart", "touchend", "touchcancel"]) {
                document.removeEventListener(type, countFingers, { capture: true });
            }

            document.removeEventListener(PANE_CHANGE_EVENT, requestSync);
            document.removeEventListener("keydown", onKeyDown, true);
            darkScheme.removeEventListener("change", onSchemeChange);
            document.removeEventListener("visibilitychange", onVisibilityChange);
            map.remove();
            markers.clear();
        },
    };
}
