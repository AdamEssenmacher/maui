# Android ModalNavigationManager HandlerChanged Retention Repro

This repro isolates the Android/Windows `ModalNavigationManager` platform-readiness token path. The current code subscribes to `CurrentPlatformPage.HandlerChanged`, but its cleanup action re-evaluates `CurrentPlatformPage` later. If the current platform page changes before the token is disposed, cleanup unsubscribes the wrong page and the original page keeps the modal manager subscribed.

The app keeps the original page alive to model application-owned pages. Current MAUI leaves that page's `HandlerChanged` event rooting the modal manager, window, root page, and a 1 MiB payload per attempt. The control path replaces the token with one that captures and unsubscribes the originally subscribed page.
