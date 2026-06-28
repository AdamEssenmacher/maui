# HandlerToRendererShim Element Retention Repro

This Mac Catalyst sample proves that disposing the iOS compatibility `HandlerToRendererShim` leaves its strong `Element` property assigned. If the disposed shim remains rooted, the old MAUI view and its binding-context payload graph remain alive even though the wrapped handler disconnected its virtual view.

The harness creates 80 realistic content-view payloads with 1 MiB session buffers, disposes their shims, and keeps the disposed shim peers rooted. The control path explicitly clears the stale `Element` backing field after disposal. Current MAUI leaves the field assigned.

Run:

```bash
dotnet run --project src/Controls/samples/HandlerToRendererShimElementRetentionRepro/HandlerToRendererShimElementRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

Results are written to `Path.GetTempPath()/handlertorenderershim-element-retention-results.txt`.
