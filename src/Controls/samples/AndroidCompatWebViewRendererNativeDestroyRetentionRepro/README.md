# Android Compatibility WebViewRenderer Native Destroy Retention Repro

This repro exercises the obsolete Android compatibility `WebViewRenderer` disposal path.

It creates retained native `Android.Webkit.WebView` peers with realistic in-memory HTML payloads and compares:

- control cleanup: renderer dispose plus explicit native cleanup (`SetWebViewClient(null)`, `SetWebChromeClient(null)`, `StopLoading()`, remove parent/children, `Destroy()`);
- current MAUI cleanup: `WebViewRenderer.Dispose()` only.

The autorun writes results to `files/autorun-results.txt`.
