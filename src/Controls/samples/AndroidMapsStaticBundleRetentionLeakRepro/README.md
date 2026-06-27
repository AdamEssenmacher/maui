# Android Maps Static Bundle Retention Leak Repro

This repro proves that compatibility Android Maps keeps the first `Bundle` passed to `FormsMaps.Init(Activity, Bundle)` in the static `MapRenderer.s_bundle` field for the process lifetime.

The app creates a launch-style `Bundle` containing parcelable saved-state objects with 80 MiB of managed payload, calls the real `FormsMaps.Init` path, and compares the current behavior against a control that clears the static bundle field after initialization. Results are written to `files/autorun-results.txt`.
