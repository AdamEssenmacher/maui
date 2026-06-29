# Android Shell Flyout Global Layout Listener Retention Repro

This repro exercises `ShellFlyoutTemplatedContentRenderer` on Android. The renderer creates a `GenericGlobalLayoutListener` for delayed flyout content loading and stores it only in a local variable. If the renderer is disconnected before that listener reaches the callback path that invalidates itself, a retained native flyout root can keep the renderer, Shell graph, and Shell binding payload alive.

The control run dispatches the initial global-layout callbacks before disconnect so the listener invalidates itself. The current run disconnects before those callbacks complete. Both runs retain only native flyout root views and remove unrelated app-bar and layout-changing callback roots to isolate the deferred global-layout listener.
