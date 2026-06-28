# Items2 Controller ItemsSource Retention Repro

This sample proves that a retained disposed Items2 iOS/Mac Catalyst controller keeps its disposed `ItemsSource` wrapper assigned. The wrapper then retains the app item source and item payloads.

Run:

```sh
dotnet run --project src/Controls/samples/Items2ControllerItemsSourceRetentionRepro/Items2ControllerItemsSourceRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

The app writes the result to the temp file shown by `ReproSession.ResultsPath`.
