# Android Maps Static Bundle Retention Leak Repro

This repro proves that Android Maps keeps launch `Bundle` instances in static fields for the process lifetime:

- Compatibility Maps: `FormsMaps.Init(Activity, Bundle)` stores the first `Bundle` in `Microsoft.Maui.Controls.Compatibility.Maps.Android.MapRenderer.s_bundle`.
- Current Maps: `UseMauiMaps().OnCreate` stores the launch `Bundle` in `Microsoft.Maui.Maps.Handlers.MapHandler.s_bundle`.

The app creates launch-style `Bundle` instances containing parcelable saved-state objects with 80 MiB of managed payload, runs both real assignment paths, and compares the current behavior against a control that clears the static bundle fields after initialization. Results are written to `files/autorun-results.txt`.
