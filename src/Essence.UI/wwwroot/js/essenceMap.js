// The MapLibre map behind MapInterop.cs. C# decides what to show (markers arrive preformatted); this module draws it
// and reports what the user does: map moves, marker clicks, and dismissals (a click on the map, or Escape).
import { Map as MapLibreMap, Marker, NavigationControl } from "../lib/maplibre-gl/maplibre-gl.mjs";

const VIEWPORT_DEBOUNCE_MS = 250;
const PANE_SETTLE_MS = 260;
const VISIBLE_MARGIN_PX = 48;

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
    let viewportTimer = 0;

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
        if (!e.originalEvent.target.closest?.(".ess-marker")) {
            call("OnDismiss");
        }
    }

    function onKeyDown(e) {
        if (e.key === "Escape") {
            call("OnDismiss");
        }
    }

    function onSchemeChange(e) {
        map.setStyle(e.matches ? options.darkStyleUrl : options.styleUrl);
    }

    // The part of the map not covered by the details pane (a side panel or a bottom sheet), in container pixels.
    // Called after the pane's entrance transition (PANE_SETTLE_MS) so its final position is measured.
    function uncoveredArea() {
        const area = container.getBoundingClientRect();
        let right = area.width;
        let bottom = area.height;
        const pane = document.querySelector("[data-ess-pane]");
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

    function renderMarker(element, model) {
        element.dataset.tone = model.tone;
        element.classList.toggle("is-unavailable", model.unavailable);
        element.classList.toggle("is-selected", model.id === selectedId);
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
        element.replaceChildren(pill);
    }

    map.on("load", reportViewport);
    map.on("moveend", reportViewport);
    map.on("zoom", updateCompact);
    map.on("click", onMapClick);
    document.addEventListener("keydown", onKeyDown);
    darkScheme.addEventListener("change", onSchemeChange);
    updateCompact();

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
            }
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
            setTimeout(() => {
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

        // Pans just enough that the point isn't under the details pane.
        ensureVisible(lat, lon) {
            setTimeout(() => {
                const { right, bottom } = uncoveredArea();
                const point = map.project([lon, lat]);
                const inside = point.x >= VISIBLE_MARGIN_PX && point.x <= right - VISIBLE_MARGIN_PX
                    && point.y >= VISIBLE_MARGIN_PX && point.y <= bottom - VISIBLE_MARGIN_PX;
                if (!inside) {
                    map.panBy([point.x - right / 2, point.y - bottom / 2]);
                }
            }, PANE_SETTLE_MS);
        },

        dispose() {
            clearTimeout(viewportTimer);
            document.removeEventListener("keydown", onKeyDown);
            darkScheme.removeEventListener("change", onSchemeChange);
            map.remove();
            markers.clear();
        },
    };
}
