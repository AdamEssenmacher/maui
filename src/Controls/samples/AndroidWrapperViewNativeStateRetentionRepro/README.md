# Android WrapperView Native State Retention Repro

This repro proves that retained Android `WrapperView` peers keep MAUI `Clip` and `Shadow` objects alive after `ViewHandler` disconnect.

It creates transient `BoxView` handlers with payload-bearing `Geometry` and `Shadow` instances, retains only the native `WrapperView` containers, then compares current disconnect behavior with an explicit native wrapper state clear.

The app writes its autorun result to `files/autorun-results.txt`.
