# Android RadioButtonHandler Native Text Retention Repro

This sample proves that current Android `RadioButtonHandler` can leave large `RadioButton.Content` strings copied into native `AppCompatRadioButton.Text` after handler disconnect.

The repro uses the public `RadioButton.Content` surface. On Android, current `RadioButtonHandler.MapContent(...)` calls `RadioButtonExtensions.UpdateContent(...)`, which assigns `platformRadioButton.Text = $"{radioButton.Content}"`. `RadioButtonHandler.DisconnectHandler(...)` only removes the checked-change event and does not clear the native text slot.

## What It Does

- Creates 1,024 `RadioButton` instances with generated 16K-character content labels.
- Uses real current `RadioButtonHandler` instances and the normal Android content mapper.
- Retains only the native Android `AppCompatRadioButton` peers through JNI global refs.
- Drops the managed radio buttons and handlers.
- Compares current MAUI disconnect against a control that clears only `platformRadioButton.Text = string.Empty` before disconnect.

## Build

```bash
dotnet build src/Controls/samples/AndroidRadioButtonHandlerNativeTextRetentionRepro/AndroidRadioButtonHandlerNativeTextRetentionRepro.csproj \
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
  Native AppCompatRadioButtons retained by JNI global refs: 1024/1024
  Assigned native text slots: 0/1024
  Payload-sized native text slots: 0/1024
  Retained native text payload: 0.0 KiB
  Managed RadioButton wrappers alive: 0/1024
  Managed RadioButtonHandler wrappers alive: 0/1024

Current MAUI (current MAUI disconnect)
  Native AppCompatRadioButtons retained by JNI global refs: 1024/1024
  Assigned native text slots: 1024/1024
  Payload-sized native text slots: 1024/1024
  Retained native text payload: 32.0 MiB
  Managed RadioButton wrappers alive: 0/1024
  Managed RadioButtonHandler wrappers alive: 0/1024

Verdict: PROVED
```

The retained managed object counts stay at zero, so the proof isolates stale Android native `AppCompatRadioButton.Text` state rather than a retained MAUI radio button or handler graph.
