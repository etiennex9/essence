// The MapLibre map behind MapInterop.cs. C# decides what to show (markers and a trip's route arrive preformatted); this
// module draws it and reports what the user does: map moves, marker clicks, a press and hold on the map, and dismissals
// (a click on the map, or Escape), and when the page is hidden or shown again. It also reloads the page for a new release, keeping the view (UpdateCheck.cs): here
// rather than in a module of its own, which a tab would import from the new release only when it's time to reload.
import { Map as MapLibreMap, Marker, NavigationControl } from "../lib/maplibre-gl/maplibre-gl.mjs";

const VIEWPORT_DEBOUNCE_MS = 250;
const PANE_SETTLE_MS = 260;
const VISIBLE_MARGIN_PX = 48;

// fitBounds keeps the points this far inside the area left uncovered: a marker's pill and flag stand above its point.
const FIT_PADDING_PX = 56;
const FIT_TOP_PX = 130; // under the toolbar, with room for a flag
const FIT_MAX_ZOOM = 15;

// Press and hold (a right-click on desktops, a long press on Android, a touch timer on iOS, which has neither) picks a
// destination. A finger that moves farther than this, or a second finger, is panning or zooming instead.
const HOLD_MS = 550;
const HOLD_SLOP_PX = 10;

// Android fires contextmenu around when the touch timer does: a second hold this soon after is the same one. The click
// that ends a hold (when the finger lifts, however long after) mustn't count as a click on the map, which would close the
// pane.
const HOLD_REPEAT_MS = 800;
const HOLD_CLICK_MS = 700;

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
const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");

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

    const markers = new Map(); // id → { marker, element, key }
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
    let heldThisTouch = false;
    let lastHoldAt = 0;
    let swallowClickUntil = 0;

    // Best near you's pick and closest, with their flags' text, and the stations listed with them (null: none fade).
    let emphasis = { pickId: null, closestId: null, listed: null, bestFlag: "", closestFlag: "" };
    let viewportTimer = 0;

    // flyTo and ensureVisible calls waiting to start their move (see reload).
    let pendingMoves = 0;

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

    function updateCompact() {
        container.classList.toggle("ess-map--compact", map.getZoom() < options.compactBelowZoom);
    }

    function onMapClick(e) {
        if (Date.now() < swallowClickUntil) {
            swallowClickUntil = 0;
            return;
        }

        if (!e.originalEvent.target.closest?.(".ess-marker")) {
            call("OnDismiss");
        }
    }

    // A press and hold at a point of the container (pixels): C# drops a pin there.
    function held(point) {
        const now = Date.now();
        if (now - lastHoldAt < HOLD_REPEAT_MS) {
            return;
        }

        lastHoldAt = now;
        heldThisTouch = true;
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

    function onTouchStart(e) {
        cancelHold();
        heldThisTouch = false;
        if (e.touches.length !== 1 || e.target.closest?.(NOT_HELD)) {
            return;
        }

        const touch = e.touches[0];
        const rect = container.getBoundingClientRect();
        holdStart = { x: touch.clientX, y: touch.clientY, point: [touch.clientX - rect.left, touch.clientY - rect.top] };
        holdTimer = setTimeout(() => {
            holdTimer = 0;
            held(holdStart.point);
        }, HOLD_MS);
    }

    function onTouchEnd() {
        cancelHold();
        if (heldThisTouch) {
            heldThisTouch = false;
            swallowClickUntil = Date.now() + HOLD_CLICK_MS;
        }
    }

    function onTouchMove(e) {
        if (!holdTimer) {
            return;
        }

        const touch = e.touches[0];
        if (e.touches.length !== 1 || Math.hypot(touch.clientX - holdStart.x, touch.clientY - holdStart.y) > HOLD_SLOP_PX) {
            cancelHold();
        }
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
        // typed, it clears the text (PlacesPanel), and the next one closes the pane. An open menu or info bubble in the
        // pane's header takes it first: essencePopup.js stops it at the window, so it never gets here.
        if (e.key === "Escape" && !document.querySelector("dialog:modal") && !(e.target.matches?.("input[type=search]") && e.target.value)) {
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

    // The part of the map not covered by the details pane (a side panel or a bottom sheet), in container pixels; the whole
    // map without besidePane. Called after the pane's entrance transition (PANE_SETTLE_MS) so its final position is measured.
    function uncoveredArea(besidePane = true) {
        const area = container.getBoundingClientRect();
        let right = area.width;
        let bottom = area.height;
        const pane = besidePane ? document.querySelector("[data-ess-pane]") : null;
        if (pane) {
            const rect = pane.getBoundingClientRect();
            if (rect.top > area.top + area.height / 3) {
                bottom = rect.top - area.top;
            } else {
                right = rect.left - area.left;
            }
        }

        return { width: area.width, height: area.height, right, bottom };
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
        element.classList.toggle("is-favorite", model.favorite);
        element.classList.toggle("is-selected", model.id === selectedId);
        applyEmphasis(element, model.id);
        element.style.zIndex = String(model.z + 1);
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
    map.on("zoom", updateCompact);
    map.on("click", onMapClick);
    map.on("contextmenu", onContextMenu);
    map.on("movestart", cancelHold);
    map.on("style.load", addRouteLayers);
    container.addEventListener("touchstart", onTouchStart, { passive: true });
    container.addEventListener("touchmove", onTouchMove, { passive: true });
    container.addEventListener("touchend", onTouchEnd);
    container.addEventListener("touchcancel", onTouchEnd);
    // Captured, so it sees the search box's text before Blazor's own handler (which may be registered first) clears it.
    document.addEventListener("keydown", onKeyDown, true);
    darkScheme.addEventListener("change", onSchemeChange);
    document.addEventListener("visibilitychange", onVisibilityChange);
    updateCompact();
    if (document.visibilityState !== "visible") {
        // Opened in a background tab: no event comes until it's shown.
        onVisibilityChange();
    }

    return {
        // Adds, updates and removes markers so the map shows exactly `models`. Unchanged markers aren't touched.
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
                    entry = { marker, element, key: "" };
                    markers.set(model.id, entry);
                }

                const key = JSON.stringify(model);
                if (entry.key !== key) {
                    renderMarker(entry.element, model);
                    entry.marker.setLngLat([model.lon, model.lat]);
                    entry.key = key;
                }
            }

            for (const [id, entry] of markers) {
                if (!keep.has(id)) {
                    entry.marker.remove();
                    markers.delete(id);
                }
            }
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

        // With besidePane, the point ends in the middle of the area the details pane leaves uncovered.
        flyTo(lat, lon, zoom, besidePane) {
            pendingMoves++;
            setTimeout(() => {
                pendingMoves--;
                let offset = [0, 0];
                if (besidePane) {
                    const { width, height, right, bottom } = uncoveredArea();
                    offset = [right / 2 - width / 2, bottom / 2 - height / 2];
                }

                if (reducedMotion.matches) {
                    // MapLibre turns flyTo into a jump under reduced motion and drops the offset, so jump and shift here.
                    map.jumpTo({ center: [lon, lat], zoom });
                    map.panBy([-offset[0], -offset[1]], { animate: false });
                } else {
                    map.flyTo({ center: [lon, lat], zoom, offset });
                }
            }, besidePane ? PANE_SETTLE_MS : 0);
        },

        // Shows the box (degrees), with besidePane in the area the pane leaves uncovered, once the pane is in place.
        fitBounds(west, south, east, north, besidePane) {
            pendingMoves++;
            setTimeout(() => {
                pendingMoves--;
                const { width, height, right, bottom } = uncoveredArea(besidePane);
                let padding = {
                    top: FIT_TOP_PX,
                    left: FIT_PADDING_PX,
                    right: width - right + FIT_PADDING_PX,
                    bottom: height - bottom + FIT_PADDING_PX,
                };
                if (width - padding.left - padding.right < FIT_PADDING_PX || height - padding.top - padding.bottom < FIT_PADDING_PX) {
                    // Too little left beside the pane (a short window): MapLibre wouldn't move at all.
                    padding = FIT_PADDING_PX / 2;
                }

                map.fitBounds([[west, south], [east, north]], { padding, maxZoom: FIT_MAX_ZOOM, animate: !reducedMotion.matches });
            }, besidePane ? PANE_SETTLE_MS : 0);
        },

        // Pans just enough that the point isn't under the details pane: once it's in place, unless it already is
        // (waitForPane false, for a station chosen in the pane open).
        ensureVisible(lat, lon, waitForPane = true) {
            pendingMoves++;
            setTimeout(() => {
                pendingMoves--;
                const { right, bottom } = uncoveredArea();
                const point = map.project([lon, lat]);
                const inside = point.x >= VISIBLE_MARGIN_PX && point.x <= right - VISIBLE_MARGIN_PX
                    && point.y >= VISIBLE_MARGIN_PX && point.y <= bottom - VISIBLE_MARGIN_PX;
                if (!inside) {
                    map.panBy([point.x - right / 2, point.y - bottom / 2]);
                }
            }, waitForPane ? PANE_SETTLE_MS : 0);
        },

        // Reloads the page. With saveView, the page it reloads into opens on this view, with the page's own `state` (C#'s
        // ReloadState) and the user's dot. An automatic reload that can't save it doesn't happen: the page keeps offering
        // it. The view is read here because C#'s is only reported once the map has drawn, which a page that opened out of
        // sight hasn't. A move on its way isn't saved halfway: out of sight, a flight to the user or a station doesn't
        // run at all (no animation frames), and the reload starts over instead.
        reload(state, saveView, automatic) {
            if (saveView && pendingMoves === 0 && !map.isMoving()) {
                const center = map.getCenter();
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
            cancelHold();
            container.removeEventListener("touchstart", onTouchStart);
            container.removeEventListener("touchmove", onTouchMove);
            container.removeEventListener("touchend", onTouchEnd);
            container.removeEventListener("touchcancel", onTouchEnd);
            document.removeEventListener("keydown", onKeyDown, true);
            darkScheme.removeEventListener("change", onSchemeChange);
            document.removeEventListener("visibilitychange", onVisibilityChange);
            map.remove();
            markers.clear();
        },
    };
}
