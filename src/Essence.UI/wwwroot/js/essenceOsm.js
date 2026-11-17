// OpenStreetMap's sign-in for BrowserOsmSignIn.cs, in a window of its own. Its answer comes back to osm-auth.html, on this
// page's own origin, which hands the code and the state back here. OpenStreetMap's pages send Cross-Origin-Opener-Policy:
// same-origin, which cuts the window off from this page as soon as it opens them: osm-auth.html then has no
// window.opener, and the window reads as closed from here. So the answer comes through a BroadcastChannel, which every page
// of the origin shares (and through postMessage too, when the window still has its opener), and giving up is the page's
// Cancel, never the window's closing.

const CHANNEL = "essence-osm-auth";
const TYPE = "essence-osm-auth";

let pending = null;

// Opens the sign-in and resolves with { code, state, error } once its answer is in; with null when cancelled (cancel())
// or after timeoutMs; with { error: "popup_blocked" } when the window can't open. A sign-in already open is given up.
export function authorize(url, timeoutMs) {
    cancel();
    const popup = window.open(url, "essence-osm", "popup,width=520,height=720");
    if (!popup) {
        return Promise.resolve({ code: null, state: null, error: "popup_blocked" });
    }

    return new Promise(resolve => {
        const channel = "BroadcastChannel" in window ? new BroadcastChannel(CHANNEL) : null;
        const timer = setTimeout(() => finish(null), timeoutMs);

        function finish(answer) {
            clearTimeout(timer);
            channel?.close();
            window.removeEventListener("message", onMessage);
            if (pending?.finish === finish) {
                pending = null;
            }

            if (answer === null) {
                try {
                    popup.close();
                } catch {
                    // Cut off from this page: the user closes it.
                }
            }

            resolve(answer);
        }

        function accept(data) {
            if (data && data.type === TYPE) {
                finish({ code: data.code ?? null, state: data.state ?? null, error: data.error ?? null });
            }
        }

        // Only this origin's pages: osm-auth.html, never OpenStreetMap's or another site's.
        function onMessage(e) {
            if (e.origin === window.location.origin) {
                accept(e.data);
            }
        }

        if (channel) {
            channel.onmessage = e => accept(e.data);
        }

        window.addEventListener("message", onMessage);
        pending = { finish };
    });
}

// Gives up the sign-in that's open: its promise resolves with null.
export function cancel() {
    pending?.finish(null);
}
