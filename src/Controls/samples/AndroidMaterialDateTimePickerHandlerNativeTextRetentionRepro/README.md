# Android Material DatePickerHandler2/TimePickerHandler2 Native Text Retention Repro

This sample proves that Material3 Android `DatePickerHandler2` and `TimePickerHandler2` can leave formatted display text copied into native picker text slots after handler disconnect.

The repro uses the public `DatePicker.Format` and `TimePicker.Format` surfaces with `<UseMaterial3>true</UseMaterial3>`. On Android, `DatePickerExtensions.SetText(...)` assigns `MauiMaterialDatePicker.Text`, and `TimePickerExtensions.SetTimeImpl(...)` assigns `MauiMaterialTimePicker.Text`. The Material3 handler disconnect paths clean dialogs/listeners/callbacks, but do not clear the native `TextView.Text` slot.

## What It Does

- Creates 1,024 `DatePicker` instances and 1,024 `TimePicker` instances with generated 4K-character custom display format payloads.
- Uses normal handler registration and asserts the active handlers are `DatePickerHandler2` and `TimePickerHandler2`.
- Retains only the native Android `MauiMaterialDatePicker` and `MauiMaterialTimePicker` peers through JNI global refs.
- Drops the managed pickers and handlers.
- Compares current MAUI disconnect against a control that clears only `TextView.Text` before disconnect.

## Build

```bash
dotnet build src/Controls/samples/AndroidMaterialDateTimePickerHandlerNativeTextRetentionRepro/AndroidMaterialDateTimePickerHandlerNativeTextRetentionRepro.csproj \
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
Control (explicit native text clear)
  Native Material DatePicker/TimePicker peers retained by JNI global refs: 2048/2048
  Assigned native text slots: 0/2048
  Payload-sized native text slots: 0/2048
  Retained native text payload: 0.0 KiB
  Managed DatePicker/TimePicker wrappers alive: 0/2048
  Managed DatePickerHandler2/TimePickerHandler2 wrappers alive: 0/2048

Current MAUI (current MAUI disconnect)
  Native Material DatePicker/TimePicker peers retained by JNI global refs: 2048/2048
  Assigned native text slots: 2048/2048
  Payload-sized native text slots: 2048/2048
  Retained native text payload: 16.0 MiB
  Managed DatePicker/TimePicker wrappers alive: 0/2048
  Managed DatePickerHandler2/TimePickerHandler2 wrappers alive: 0/2048

Verdict: PROVED
```

The retained managed object counts stay at zero, so the proof isolates stale Android native `MauiMaterialDatePicker.Text` / `MauiMaterialTimePicker.Text` state rather than retained MAUI picker or handler graphs.
