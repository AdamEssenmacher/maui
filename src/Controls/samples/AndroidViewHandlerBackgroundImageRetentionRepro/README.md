# Android ViewHandler Background Image Retention Repro

This sample proves that current Android page/view handlers can leave large image backgrounds assigned to native `View.Background` after handler disconnect.

The repro uses the public `ContentPage.BackgroundImageSource` surface. On Android, the current handler stack maps that through `ViewHandler.MapBackground(...)` and `ViewExtensions.UpdateBackgroundImageSourceAsync(...)`, which assigns the loaded drawable to the platform view background. `PageHandler`, `ContentViewHandler`, and the shared Android `ViewHandler` disconnect path do not clear that native background slot.

## What It Does

- Creates 96 `ContentPage` instances with generated 512x512 ARGB backgrounds.
- Uses real current `PageHandler` instances and the normal image-source service path.
- Retains only the native Android page views through JNI global refs.
- Drops the managed pages, handlers, and image sources.
- Compares current MAUI disconnect against a control that clears only `platformView.Background = null` before disconnect.

## Build

```bash
dotnet build src/Controls/samples/AndroidViewHandlerBackgroundImageRetentionRepro/AndroidViewHandlerBackgroundImageRetentionRepro.csproj \
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
Control (explicit native background clear)
  Native views retained by JNI global refs: 96/96
  Assigned native background slots: 0/96
  Payload-sized native background slots: 0/96
  Retained native background payload: 0.0 MiB
  Managed Page wrappers alive: 0/96
  Managed PageHandler wrappers alive: 0/96
  Managed image sources alive: 0/96

Current MAUI (current MAUI disconnect)
  Native views retained by JNI global refs: 96/96
  Assigned native background slots: 96/96
  Payload-sized native background slots: 96/96
  Retained native background payload: 96.0 MiB
  Managed Page wrappers alive: 0/96
  Managed PageHandler wrappers alive: 0/96
  Managed image sources alive: 0/96

Image service results created: 576
Image service results disposed by MAUI: 0

Verdict: PROVED
```

The retained managed object counts stay at zero, so the proof isolates stale Android native background drawable state rather than a retained MAUI page/handler/source graph.
