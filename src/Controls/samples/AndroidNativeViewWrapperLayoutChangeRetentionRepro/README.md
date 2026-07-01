# Android NativeViewWrapper LayoutChange Retention Repro

This repro isolates the compatibility Android native view wrapper renderer path:

- `src/Compatibility/Core/src/Android/NativeViewWrapperRenderer.cs`
- `src/Compatibility/Core/src/Android/ViewRenderer.cs`

`NativeViewWrapperRenderer.OnElementChanged()` wraps an app-provided Android `View`, calls `SetNativeControl(Element.NativeView)`, and subscribes an anonymous `Control.LayoutChange` lambda that captures the renderer through `Element`. `ViewRenderer.SetNativeControl()` also assigns `Control.OnFocusChangeListener = this`. Because `NativeViewWrapperRenderer.ManageNativeControlLifetime` is `false`, dispose does not clear the native control focus listener, and the anonymous layout-change lambda cannot be unsubscribed.

The app retains only the app-provided native `View` peers with JNI global refs, matching the ownership model implied by `NativeViewWrapperRenderer.ManageNativeControlLifetime == false`. The control renderer avoids the anonymous layout event and explicitly clears the focus listener before disposal. The current MAUI path uses the real renderer. If the current path retains disposed renderer instances while the explicit cleanup control releases them, the leak is proved.

Run:

```bash
dotnet build src/Controls/samples/AndroidNativeViewWrapperLayoutChangeRetentionRepro/AndroidNativeViewWrapperLayoutChangeRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true -m:1 -nr:false -t:SignAndroidPackage -v:minimal -clp:Summary
```

Then install and launch the signed APK. Results are written to `files/autorun-results.txt` inside the app sandbox.
