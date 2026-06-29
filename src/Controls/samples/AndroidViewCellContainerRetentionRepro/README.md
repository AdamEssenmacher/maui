# Android ViewCellContainer Retention Repro

This repro exercises the legacy Android `ViewCellRenderer` path and keeps only native `ViewCellContainer` peers alive after renderer disconnect.

Each cycle creates a `ViewCell` with a child `BoxView`, a 512 KiB cell binding payload, a 512 KiB child-view binding payload, and a 512 KiB per-cycle `MauiContext` service-provider payload. Current `ViewCellContainer.DisconnectHandler()` removes the child native view and disconnects the child handler, but leaves `_viewCell`, `_viewHandler`, and `_currentView` assigned. The control run clears those container fields after disconnect.

Run with:

```sh
dotnet build src/Controls/samples/AndroidViewCellContainerRetentionRepro/AndroidViewCellContainerRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```

The app writes its autorun result to `files/autorun-results.txt`.
