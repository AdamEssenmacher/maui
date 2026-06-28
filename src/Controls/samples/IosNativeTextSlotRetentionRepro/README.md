# iOS Native Text Slot Retention Repro

This Mac Catalyst sample demonstrates that current iOS handlers detach events during disconnect but leave large native text slots assigned on retained native `UILabel`, `UITextField`, and `UITextView` peers.

The autorun scenario creates `Label`, `Entry`, and `Editor` controls with realistic 512 KiB text payloads, disconnects their handlers, clears the MAUI virtual-view text, and keeps only the native text controls alive. The control path explicitly clears native `Text`, `AttributedText`, and placeholder text slots before retaining the native peers.

Run:

```bash
IOS_NATIVE_TEXT_SLOT_RETENTION_REPRO_AUTORUN=1 \
IOS_NATIVE_TEXT_SLOT_RETENTION_REPRO_RESULTS=/tmp/ios-native-text-slot-retention-results.txt \
dotnet run --project src/Controls/samples/IosNativeTextSlotRetentionRepro/IosNativeTextSlotRetentionRepro.csproj \
  -f net10.0-maccatalyst \
  -p:UseMaui=false \
  -p:IncludeMacCatalystTargetFrameworks=true \
  -m:1 \
  -nr:false
```
