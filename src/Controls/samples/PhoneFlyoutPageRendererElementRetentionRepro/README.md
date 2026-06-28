# PhoneFlyoutPageRenderer Element Retention Repro

This Mac Catalyst sample proves that disposing the legacy iOS `PhoneFlyoutPageRenderer` leaves its `Element` property assigned. If the disposed native renderer peer remains rooted, the old MAUI `FlyoutPage` and its binding-context payload graph remain alive.

The harness creates 80 realistic flyout/detail page payloads with 1 MiB session buffers, disposes their renderers, and keeps the disposed native renderer peers rooted. The control path explicitly clears the stale `Element` backing field after disposal. Current MAUI leaves the field assigned.

Run:

```bash
dotnet run --project src/Controls/samples/PhoneFlyoutPageRendererElementRetentionRepro/PhoneFlyoutPageRendererElementRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

Results are written to `Path.GetTempPath()/phoneflyoutpagerenderer-element-retention-results.txt`.
