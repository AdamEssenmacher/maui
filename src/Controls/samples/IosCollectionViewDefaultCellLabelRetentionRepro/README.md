# iOS CollectionView DefaultCell Label Retention Repro

This repro proves that retained iOS/Mac Catalyst `CollectionView` default native cells keep their last `UILabel.Text` payload after the MAUI item graph is gone.

Static paths:

- `src/Controls/src/Core/Handlers/Items/iOS/ItemsViewController.cs` assigns `DefaultCell.Label.Text = ItemsSource[indexPath].ToString()` in `UpdateDefaultCell()`.
- `src/Controls/src/Core/Handlers/Items2/iOS/ItemsViewController2.cs` assigns `DefaultCell2.Label.Text = ItemsSource[indexpathAdjusted].ToString()` in `GetCell()`.
- `DefaultCell`, `DefaultCell2`, `ItemsViewCell`, and `ItemsViewCell2` do not clear the retained native label text when cells are disposed or retained by UIKit reuse/native lifetime.

Run:

```bash
dotnet build src/Controls/samples/IosCollectionViewDefaultCellLabelRetentionRepro/IosCollectionViewDefaultCellLabelRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
artifacts/bin/IosCollectionViewDefaultCellLabelRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/IosCollectionViewDefaultCellLabelRetentionRepro.app/Contents/MacOS/IosCollectionViewDefaultCellLabelRetentionRepro
```

The result file is written to `/tmp/ios-collectionview-defaultcell-label-retention-results.txt`.
