# iOS CollectionView Supplementary Label Retention Repro

This repro proves that retained iOS/Mac Catalyst `CollectionView` default supplementary native cells keep their last `UILabel.Text` payload after the MAUI source/header/footer objects are gone.

Static paths:

- `src/Controls/src/Core/Handlers/Items/iOS/GroupableItemsViewController.cs` assigns `DefaultCell.Label.Text = ItemsSource.Group(indexPath).ToString()` in `UpdateDefaultSupplementaryView()`.
- `src/Controls/src/Core/Handlers/Items2/iOS/GroupableItemsViewController2.cs` assigns `DefaultCell2.Label.Text = ItemsSource?.Group(indexPath)?.ToString()` in `UpdateDefaultSupplementaryView()`.
- `src/Controls/src/Core/Handlers/Items2/iOS/StructuredItemsViewController2.cs` assigns `DefaultCell2.Label.Text = Header/Footer.ToString()` in `UpdateDefaultSupplementaryView()`.
- `DefaultCell`, `DefaultCell2`, `ItemsViewCell`, and `ItemsViewCell2` do not clear retained native supplementary label text when cells are retained by UIKit reuse/native lifetime.

Run:

```bash
dotnet build src/Controls/samples/IosCollectionViewSupplementaryLabelRetentionRepro/IosCollectionViewSupplementaryLabelRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosCollectionViewSupplementaryLabelRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosCollectionViewSupplementaryLabelRetentionRepro.app/Contents/MacOS/IosCollectionViewSupplementaryLabelRetentionRepro
```

The result file is written to `/tmp/ios-collectionview-supplementary-label-retention-results.txt`.
