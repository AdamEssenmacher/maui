# Android ShellSearchView native text retention repro

This repro exercises the Android compatibility Shell search view path that creates an internal `AppCompatAutoCompleteTextView` for `SearchHandler`.

`ShellSearchView.LoadView()` assigns `SearchHandler.Query` and `Placeholder` directly to the native text field. `ShellSearchView.Dispose()` removes listeners and disposes the child controls, but it does not clear the native `Text` or `Hint` slots first.

The repro retains only the native `EditText` peers after disposal. The Shell search suggestion adapter is replaced with a zero-row adapter in both runs so the proof stays isolated from the separately tracked Shell search adapter/reload behavior. The control run explicitly clears the native text/hint slots before disposal; the current run uses MAUI cleanup as-is.
