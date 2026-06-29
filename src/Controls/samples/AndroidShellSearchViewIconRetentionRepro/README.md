# Android ShellSearchView icon retention repro

This repro exercises the Android compatibility Shell search view path that creates native `ImageButton` peers for `SearchHandler.QueryIcon`, `ClearIcon`, and `ClearPlaceholderIcon`.

`ShellSearchView.LoadView()` loads each custom icon and assigns the result to the native image button with `SetImageDrawable(...)`. `ShellSearchView.Dispose()` removes listeners and disposes the child controls, but it does not clear those native image slots first.

The repro retains only the native `ImageButton` peers after disposal. The Shell search suggestion adapter is replaced with a zero-row adapter in both runs so the proof stays isolated from the separately tracked Shell search adapter/reload behavior. The control run explicitly clears the native image button drawables before disposal; the current run uses MAUI cleanup as-is.
