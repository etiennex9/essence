// Device services for BrowserDevice.cs: geolocation and localStorage.

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
