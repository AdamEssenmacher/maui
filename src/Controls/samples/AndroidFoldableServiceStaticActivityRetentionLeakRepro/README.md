# Android FoldableService Static Activity Retention Leak Repro

This sample proves that Android foldable support can retain a destroyed `Activity`.

`UseFoldable()` stores the scoped foldable service in `DualScreenInfo.Current` on resume. The service keeps `_mainActivity` strongly. Foldable initialization also assigns a static `DefaultHingeSensor` and subscribes its event to an instance method on the service. When the activity is destroyed, neither static root is cleared.

The repro creates real internal `FoldableService` instances, assigns their private `_mainActivity` field the same way the Android initialization path does, stores them through the real `DualScreenInfo.Current.SetFoldableService` method, and separately wires the real `DefaultHingeSensor.OnSensorChanged` event to the service instance. The control path clears both static roots after teardown and forces full GC.
