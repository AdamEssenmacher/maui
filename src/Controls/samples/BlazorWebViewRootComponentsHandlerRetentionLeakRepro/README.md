# BlazorWebView RootComponents Handler Retention Leak Repro

This repro exercises a handler-lifecycle leak in `BlazorWebViewHandler`.

`BlazorWebViewHandler.RootComponents` subscribes to `RootComponentsCollection.CollectionChanged` when the handler maps a `BlazorWebView`. Platform disconnect paths dispose the platform WebView manager, but they do not unsubscribe from the retained `RootComponents` collection. If a `BlazorWebView` is kept alive while handlers are repeatedly disconnected and replaced, the collection keeps every old handler alive. Those handlers retain their old `MauiContext` and service-provider graph.

The repro creates 80 disconnected Mac Catalyst handlers against one retained `BlazorWebView`. Each handler receives a realistic 1 MiB payload through its scoped service provider. The harness uses a small `BlazorWebViewHandler` subclass with a neutral `WKWebViewConfiguration` so the already-known iOS native WebKit hook retention class does not mask this path. The control path explicitly removes the `CollectionChanged` handler after disconnect; the leak path leaves MAUI's subscription intact.
