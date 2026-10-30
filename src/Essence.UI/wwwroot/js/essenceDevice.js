// Device services for BrowserDevice.cs: geolocation, localStorage and sessionStorage.

export function getPosition(timeoutMs) {
    return new Promise(resolve => {
        if (!("geolocation" in navigator)) {
            resolve({ status: "unavailable" });
            return;
        }

        navigator.geolocation.getCurrentPosition(
            p => resolve({ status: "ok", latitude: p.coords.latitude, longitude: p.coords.longitude, accuracyMeters: p.coords.accuracy }),
            e => resolve({ status: e.code === e.PERMISSION_DENIED ? "denied" : "unavailable" }),
            { enableHighAccuracy: false, timeout: timeoutMs, maximumAge: 60_000 });
    });
}

// Storage can be blocked (private windows, site data turned off). Preferences are a convenience, so failures are ignored.
export function getItem(key) {
    try {
        return localStorage.getItem(key);
    } catch {
        return null;
    }
}

export function setItem(key, value) {
    try {
        localStorage.setItem(key, value);
    } catch {
        // Not saved; the default applies next time.
    }
}

// The tab's own storage: an open trip, which must outlive Android discarding the tab while Google Maps navigates. Blocked
// storage loses it, like the preferences.
export function getSessionItem(key) {
    try {
        return sessionStorage.getItem(key);
    } catch {
        return null;
    }
}

export function setSessionItem(key, value) {
    try {
        sessionStorage.setItem(key, value);
    } catch {
        // Not kept; a restored tab opens without it.
    }
}

export function removeSessionItem(key) {
    try {
        sessionStorage.removeItem(key);
    } catch {
        // Nothing was kept.
    }
}
