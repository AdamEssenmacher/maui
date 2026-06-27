# MultiPage ItemsSource reset retention leak repro

This sample proves that `TabbedPage`/`MultiPage<T>` generated pages can remain in the logical tree when an `ItemsSource` reset calls `InternalChildren.Clear()`.

Run:

```sh
dotnet build src/Controls/samples/MultiPageItemsSourceResetRetentionLeakRepro/MultiPageItemsSourceResetRetentionLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/MultiPageItemsSourceResetRetentionLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/MultiPageItemsSourceResetRetentionLeakRepro.app --args --results=/tmp/multipageitemssourceresetretentionleakrepro-results.txt
cat /tmp/multipageitemssourceresetretentionleakrepro-results.txt
```
