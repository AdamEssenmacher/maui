# Android Border StrokeShape Drawable Retention Repro

This repro proves that retained Android `ContentViewGroup` peers can keep `Border.StrokeShape` object graphs alive through the stale native `View.Background` slot after `BorderHandler` disconnect.

It creates transient current-handler `Border` controls with payload-bearing custom `Geometry` stroke shapes, disconnects the handlers, clears the already-known `ContentViewGroup.CrossPlatformLayout` and `ContentViewGroup.Clip` owner fields in both scenarios, then compares current MAUI behavior with an explicit native background clear.

The app writes its autorun result to `files/autorun-results.txt`.
