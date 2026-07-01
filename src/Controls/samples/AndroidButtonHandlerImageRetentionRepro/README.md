# Android ButtonHandler Image Retention Repro

This sample proves that current Android `ButtonHandler` can leave large button images assigned to native `MaterialButton.Icon` after handler disconnect.

The repro uses the public `Button.ImageSource` surface. On Android, current `ButtonHandler.MapImageSource(...)` loads the image through `ImageSourcePartLoader` and assigns the loaded drawable to `MaterialButton.Icon`. `ButtonHandler.DisconnectHandler(...)` calls `ImageSourceLoader.Reset()`, but that only disposes/cancels the image-source result; it does not call the image-source setter with `null`, so the native icon slot remains assigned.

## What It Does

- Creates 96 `Button` instances with generated 512x512 ARGB image sources.
- Uses real current `ButtonHandler` instances and the normal image-source service path.
- Retains only the native Android `MaterialButton` peers through JNI global refs.
- Drops the managed buttons, handlers, and image sources.
- Compares current MAUI disconnect against a control that clears only `platformButton.Icon = null` before disconnect.

## Build

```bash
dotnet build src/Controls/samples/AndroidButtonHandlerImageRetentionRepro/AndroidButtonHandlerImageRetentionRepro.csproj \
  -f net10.0-android \
  -p:UseMaui=false \
  -p:IncludeAndroidTargetFrameworks=true \
  -p:EmbedAssembliesIntoApk=true \
  -m:1 \
  -nr:false \
  -t:SignAndroidPackage \
  -v:minimal \
  -clp:Summary
```

## Observed Result

Run on `Maui_Tiny_API33` with actual guest memory `MemTotal: 983584 kB`.

```text
Control (explicit native icon clear)
  Native MaterialButtons retained by JNI global refs: 96/96
  Assigned native icon slots: 0/96
  Payload-sized native icon slots: 0/96
  Retained native icon payload: 0.0 MiB
  Managed Button wrappers alive: 0/96
  Managed ButtonHandler wrappers alive: 0/96
  Managed image sources alive: 0/96

Current MAUI (current MAUI disconnect)
  Native MaterialButtons retained by JNI global refs: 96/96
  Assigned native icon slots: 96/96
  Payload-sized native icon slots: 96/96
  Retained native icon payload: 96.0 MiB
  Managed Button wrappers alive: 0/96
  Managed ButtonHandler wrappers alive: 0/96
  Managed image sources alive: 0/96

Image service results created: 192
Image service results disposed by MAUI: 192

Verdict: PROVED
```

The retained managed object counts stay at zero, so the proof isolates stale Android native `MaterialButton.Icon` drawable state rather than a retained MAUI button/handler/source graph.
