// The Android app's side of the page (MainPage.xaml.cs). Back closes a dialog open over the page first, as Chrome does
// on the web; the WebView doesn't, so the app is told when one is open, and closes it through closeDialog.
(() => {
    let reported = false;

    function report() {
        const open = document.querySelector("dialog[open]") !== null;
        // Before Blazor starts there's no one to tell; the next change reports again.
        if (open === reported || !window.DotNet) {
            return;
        }

        reported = open;
        DotNet.invokeMethodAsync("Essence.Mobile", "DialogOpenChanged", open).catch(() => {
            reported = !open;
        });
    }

    // The dialogs are always in the page (AboutDialog.razor); only their open attribute changes.
    new MutationObserver(report).observe(document.documentElement, { subtree: true, attributes: true, attributeFilter: ["open"] });

    window.essenceHost = {
        closeDialog() {
            const open = document.querySelectorAll("dialog[open]");
            open[open.length - 1]?.close();
        },
    };
})();
