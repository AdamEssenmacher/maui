# Android WebChromeClient Custom View Retention Leak Repro

This repro proves that Android `MauiWebChromeClient.Disconnect()` can leave fullscreen custom content attached to the activity decor view after WebView handler disconnect.

Each iteration creates a `MauiWebChromeClient`, calls `OnShowCustomView()` with a custom native view carrying a 1 MiB managed payload, then simulates WebView disconnect. The control path calls `OnHideCustomView()` before `Disconnect()`. The current MAUI path calls only `Disconnect()`, matching `WebViewHandler.DisconnectHandler()`.
