# MAUI #32726 CollectionViewHandler2 Repro

This standalone app demonstrates the suspected regression introduced by dotnet/maui #32726
with a more realistic adaptive `CollectionView` scenario.

The project lives under `repros/Maui32726Repro` and references the MAUI source
from the repo checkout that contains it.

## Build

```bash
cd repros/Maui32726Repro
dotnet build -f net10.0-maccatalyst
```

## Run

```bash
cd repros/Maui32726Repro
dotnet run -f net10.0-maccatalyst
```

The app launches in manual mode. Click `Restore Catalog` to run the repro flow.

The app also writes the latest result inside its Mac Catalyst app container. To find and read it:

```bash
find "$HOME/Library/Containers/com.companyname.maui32726repro/Data" -name maui32726-repro-result.txt -print -exec sed -n '1,120p' {} \;
```

For unattended launch checks, set both environment variables before running:

```bash
export MAUI32726_AUTORUN=1
export MAUI32726_EXIT_AFTER_RESULT=1
dotnet run -f net10.0-maccatalyst
```

## Scenario

The app displays an inventory catalog backed by a `CollectionView` using `GridItemsLayout`.
Clicking `Restore Catalog` runs a deterministic workflow that mirrors a cached workspace/tab
or native-host lifecycle:

1. The catalog opens in a compact adaptive width with 2 columns.
2. The app switches to another workspace and shelves the catalog native view while keeping the
   catalog workspace cached.
3. The catalog workspace is restored from that cache.
4. The restored catalog responds to a wider window by changing the grid span to 4 columns.

That final responsive span change is a normal app behavior for Mac Catalyst window resize,
iPad Stage Manager/Split View-style width changes, or desktop-style adaptive layouts. The
important lifecycle detail is that a cached host reused the same `CollectionViewHandler2`
instance after it had been disconnected.

## Expected Result

On the inflight branch containing #32726, the app should report:

`REPRODUCED: adaptive catalog crashed after restore.`

In manual mode it also shows a `Reproduced #32726` alert. If you only see a blank or white
catalog area, use the result file command above; the result file is the authoritative signal.

In #32726, `CollectionViewHandler2.DisconnectHandler` clears and nulls `_layoutPropertyCache`.
When the cached catalog handler is restored and the adaptive grid updates `GridItemsLayout.Span`,
the next `ItemsLayout.PropertyChanged` dereferences that null cache in
`OnItemsLayoutPropertyChanged`.

After the handler is fixed to keep or recreate the cache, and to clear cached layout values when the layout subscription changes, the same app should report:

`PASS after fix: adaptive catalog restored and resized without exception.`
