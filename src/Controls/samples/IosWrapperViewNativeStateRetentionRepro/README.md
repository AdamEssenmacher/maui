# iOS WrapperView Native State Retention Repro

This repro exercises the iOS/Mac Catalyst `WrapperView` container path used by current handlers for `Clip`.

`ViewHandler.MapClip()` assigns app-owned `IShape` objects to `WrapperView.Clip`. `ViewHandler<T>.RemoveContainer()` calls `WrapperView.Disconnect()` during handler disconnect, but that method only clears mask layers and the border subview; it does not clear the `Clip` field.

The app compares a control scenario that clears `WrapperView.Clip` after handler disconnect with the current MAUI scenario that leaves it assigned. It retains only the native `WrapperView` peers via Objective-C retain, writes the autorun report to `/tmp/ios-wrapperview-native-state-retention-results.txt`, and exits with code 0 only when the leak is proved.
