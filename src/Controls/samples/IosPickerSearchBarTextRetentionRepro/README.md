# iOS Picker/SearchBar Native Text Slot Retention Repro

This Mac Catalyst sample demonstrates that current iOS/Mac Catalyst `PickerHandler` and `SearchBarHandler` disconnect paths detach managed event/proxy state but leave native text slots assigned on retained `MauiPicker` and `MauiSearchBar` peers.

The autorun scenario creates `Picker` and `SearchBar` controls with 512 KiB text payloads, disconnects their handlers, clears the MAUI virtual-view text/items, and keeps only the native controls alive. The control path explicitly clears native `Text`, `AttributedText`, placeholder, and picker input slots before retaining the native peers.

Run:

```bash
IOS_PICKER_SEARCHBAR_TEXT_RETENTION_REPRO_AUTORUN=1 \
IOS_PICKER_SEARCHBAR_TEXT_RETENTION_REPRO_RESULTS=/tmp/ios-picker-searchbar-text-retention-results.txt \
dotnet run --project src/Controls/samples/IosPickerSearchBarTextRetentionRepro/IosPickerSearchBarTextRetentionRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false \
  -p:IncludeMacCatalystTargetFrameworks=true \
  -m:1 \
  -nr:false
```
