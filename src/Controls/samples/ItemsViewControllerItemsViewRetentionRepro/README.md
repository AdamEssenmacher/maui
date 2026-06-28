# ItemsViewController ItemsView Retention Repro

This sample proves that disposed legacy iOS/Mac Catalyst `ItemsViewController<TItemsView>` peers keep their last MAUI `ItemsView` assigned through the get-only `ItemsView` property.

The autorun keeps disposed native controller peers alive in both scenarios to model a native `UIViewController` peer outliving renderer disposal. The control scenario clears only the stale `ItemsView` backing field after dispose. Current MAUI leaves `ItemsView` assigned because `ItemsViewController<TItemsView>.Dispose(bool)` tears down native source/delegate state but cannot clear the get-only property.

Run:

```sh
dotnet run --project src/Controls/samples/ItemsViewControllerItemsViewRetentionRepro/ItemsViewControllerItemsViewRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes results to the Mac Catalyst process temp directory as `itemsviewcontroller-itemsview-retention-results.txt`.
