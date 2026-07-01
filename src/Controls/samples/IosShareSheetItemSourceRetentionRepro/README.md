# iOS Share Sheet Item Source Retention Repro

This sample proves that retained iOS/Mac Catalyst `UIActivityViewController` peers can keep MAUI Share item-source payloads alive after the managed request path is gone.

The current scenario creates MAUI's internal `ShareActivityItemSource` with a realistic generated `ShareTextRequest.Text` payload, places it in a native share-sheet controller, and retains only the native controller peer. The control scenario retains the same native controller shape but uses a clearable item source and clears the payload after controller creation.

Run:

```bash
dotnet run --project src/Controls/samples/IosShareSheetItemSourceRetentionRepro/IosShareSheetItemSourceRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -m:1 -nr:false
```

Result file:

```text
/tmp/ios-share-sheet-itemsource-retention-results.txt
```
