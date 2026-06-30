# KeyboardAccelerators Collection Retention Repro

This sample proves that the public `MenuFlyoutItem.KeyboardAccelerators` collection can retain discarded menu items.

The repro creates 160 discarded `MenuFlyoutItem` instances with realistic 1 MiB binding payloads and three keyboard accelerators each. The app keeps only the public `KeyboardAccelerators` collection references. `KeyboardAccelerator` is a `BindableObject`, not a child element with an owner parent link, so the control run can retain the same non-empty accelerator collections without retaining the menu items.

Current MAUI keeps the menu items alive through the anonymous `CollectionChanged` handler installed by the `MenuFlyoutItem` constructor. The control run reflectively clears the same collection event fields before retaining the accelerator collections.

Expected result:

```text
RESULT: PROVEN
control: 0/160 menu items, payloads, and payload buffers retained
current: 160/160 menu items, payloads, and payload buffers retained
```

Run:

```bash
dotnet build src/Controls/samples/KeyboardAcceleratorsCollectionRetentionRepro/KeyboardAcceleratorsCollectionRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
open -W artifacts/bin/KeyboardAcceleratorsCollectionRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/KeyboardAcceleratorsCollectionRetentionRepro.app
cat /tmp/keyboardaccelerators-collection-retention-results.txt
```
