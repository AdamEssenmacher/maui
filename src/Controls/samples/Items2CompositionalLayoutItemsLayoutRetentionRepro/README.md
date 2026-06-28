# Items2 Compositional Layout ItemsLayout Retention Repro

This sample proves that the Items2 iOS/Mac Catalyst compositional layout keeps the app `ItemsLayout` alive after the native layout is disposed when the disposed native layout peer remains rooted.

Run:

```sh
dotnet run --project src/Controls/samples/Items2CompositionalLayoutItemsLayoutRetentionRepro/Items2CompositionalLayoutItemsLayoutRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to the temp file shown by `ReproSession.ResultsPath`.
