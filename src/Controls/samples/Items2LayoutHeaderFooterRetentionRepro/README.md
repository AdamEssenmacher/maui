# Items2 Layout Header/Footer Retention Repro

This sample proves that the Items2 iOS/Mac Catalyst compositional layout keeps `CollectionView.Header` and `CollectionView.Footer` views alive after the native layout is disposed when the disposed native layout peer remains rooted.

Run:

```sh
dotnet run --project src/Controls/samples/Items2LayoutHeaderFooterRetentionRepro/Items2LayoutHeaderFooterRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to the temp file shown by `ReproSession.ResultsPath`.
