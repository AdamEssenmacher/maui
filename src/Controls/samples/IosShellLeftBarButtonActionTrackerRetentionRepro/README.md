# iOS Shell left bar button action tracker retention repro

This Mac Catalyst repro isolates the `ShellPageRendererTracker.UpdateLeftToolbarItems()` native left bar button action callback.

The current path creates a native `UIBarButtonItem` with an event handler that captures the `ShellPageRendererTracker`. `ShellPageRendererTracker.Dispose()` clears Shell/page/search/back-button fields but does not clear the native left bar button action and does not clear `_fontManager`. If native left bar button peers survive navigation cleanup, the action callback can keep disposed trackers and service-provider graphs alive.

The control run lets the MAUI-created button go and keeps blank native `UIBarButtonItem` peers alive. The current run clears only the native `Image`, leaving the MAUI-created action callback intact. Payloads are 1 MiB service-provider arrays reached only through the tracker's retained `IFontManager`.
