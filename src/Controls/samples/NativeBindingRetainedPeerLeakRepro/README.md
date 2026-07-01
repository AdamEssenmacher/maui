# NativeBinding retained-peer leak repro

This repro targets the compatibility native binding path:

- `src/Controls/src/Core/Compatibility/iOS/Extensions/UIViewExtensions.cs`
- `src/Controls/src/Core/PlatformBindingHelpers.cs`

The older C018 KVO-only probe did not retain the native `UIView` peer and did not prove a leak. This repro tests the retained-peer variant: native `UITextField` peers are intentionally kept alive after the short-lived binding source is dropped. Current MAUI leaves the `PlatformBindingHelpers.BindableObjectProxy<UIView>` entry installed in a static `ConditionalWeakTable`, so the retained native peer keeps the proxy, binding source, view-model payload, and payload byte array alive.

The control path removes the native-binding proxy for the retained peer before dropping the binding source. The retained native peer remains alive in both scenarios, isolating the managed binding-payload retention.

Run:

```bash
dotnet run --project src/Controls/samples/NativeBindingRetainedPeerLeakRepro/NativeBindingRetainedPeerLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -m:1 -nr:false
```

The app writes `/tmp/nativebinding-retained-peer-leak-repro-results.txt` and exits with code `0` only when the leak is proved.
