# Android FoldableService Static Activity Retention Leak Repro

This sample proves that Android foldable support can retain a destroyed `Activity`.

`UseFoldable()` stores the scoped foldable service in `DualScreenInfo.Current` on resume. The service keeps `_mainActivity` strongly. When the activity is destroyed, that static singleton path is not cleared.

The repro creates real internal `FoldableService` instances, assigns their private `_mainActivity` field the same way the Android initialization path does, stores them through the real `DualScreenInfo.Current.SetFoldableService` method, and forces full GC. The control path clears the static `DualScreenInfo.Current` service root after teardown.
