# MediaPicker PickerRef Exception Retention Repro

This sample proves that iOS/Mac Catalyst `MediaPickerImplementation.PickerRef` can retain native picker graphs when result processing fails before task completion.

`PhotoAsync()` and `PhotosAsync()` assign the native picker to the static `PickerRef`, await a result task, and only clear `PickerRef` after the await succeeds. If the delegate result-processing path throws or is suppressed before completing the task, the async method never reaches the trailing `PickerRef?.Dispose(); PickerRef = null;` cleanup. The static picker can then keep its native delegate, callback closure, `TaskCompletionSource`, picker options, and captured app payload alive.

The autorun simulates 4 failed native picker completions with 48 MiB app payloads captured by realistic `MediaPickerOptions` objects. The control path explicitly clears `PickerRef` after the same failure. The current MAUI path leaves `PickerRef` assigned, demonstrating retained picker graphs and failed media-session payloads.

Run:

```sh
dotnet run --project src/Controls/samples/MediaPickerPickerRefExceptionRetentionRepro/MediaPickerPickerRefExceptionRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The result file is written to the process temp directory as `mediapicker-pickerref-exception-retention-results.txt`.
