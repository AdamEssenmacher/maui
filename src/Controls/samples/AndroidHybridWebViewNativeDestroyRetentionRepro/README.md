# Android HybridWebView Native Destroy Retention Repro

This repro exercises the Android `HybridWebViewHandler` disconnect path.

It creates retained native `Android.Webkit.WebView` peers with realistic in-memory HTML payloads and compares:

- control cleanup: handler disconnect plus explicit native cleanup (`SetWebViewClient(null)`, `StopLoading()`, remove parent/children, `Destroy()`);
- current MAUI cleanup: `HybridWebViewHandler.DisconnectHandler()` only.

The autorun writes results to `files/autorun-results.txt`.

The default run uses four WebViews per scenario with a 512 KiB UTF-16 HTML
payload per WebView. That keeps the low-RAM Android emulator responsive while
still proving 4.0 MiB of requested HTML payload left in retained current-path
native WebViews when `Destroy()` is not invoked.
