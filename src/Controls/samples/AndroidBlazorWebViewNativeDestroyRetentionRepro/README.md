# Android BlazorWebView Native Destroy Retention Repro

This repro exercises the Android `BlazorWebViewHandler` disconnect path.

It creates retained native `Android.Webkit.WebView` peers with realistic in-memory HTML payloads and compares:

- control cleanup: handler disconnect plus explicit native cleanup (`SetWebViewClient(null)`, `SetWebChromeClient(null)`, `StopLoading()`, remove children, `Destroy()`);
- current MAUI cleanup: `BlazorWebViewHandler.DisconnectHandler()` only.

The autorun writes results to `files/autorun-results.txt`.

The default run uses two WebViews per scenario with a 1 MiB UTF-16 HTML
payload per WebView. That keeps the low-RAM Android emulator responsive while
still proving 4.0 MiB of requested HTML payload left in retained current-path
native WebViews when `Destroy()` is not invoked.

The tracking handler uses neutral native clients to isolate this native destroy
gap from the already tracked C043 BlazorWebView handler/manager retention class.
