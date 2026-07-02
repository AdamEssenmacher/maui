# Runtime XAML DataTemplate Root Retention Repro

This sample proves a managed retention path in runtime XAML-created `DataTemplate` instances.

`ApplyPropertiesVisitor.SetTemplate()` assigns `ElementTemplate.LoadTemplate` to a closure that captures the current `HydrationContext`. That context carries `RootElement`, so if app code extracts a page-local XAML `DataTemplate` and keeps it in a registry, plugin cache, design surface, or deferred view factory, the escaped template can keep the discarded XAML root page alive.

The repro keeps 80 `DataTemplate` instances created by `ContentPage.LoadFromXaml(...)`. Each page has a 1 MiB binding-context payload. The app compares:

- a control scenario that resets `LoadTemplate` to a non-capturing factory after the template is extracted;
- the current MAUI behavior, where the runtime-XAML `LoadTemplate` closure remains installed.

The only intentionally retained objects are the extracted templates. A proven run retains the current-behavior pages, payloads, and payload buffers while the control run collects them.

## Run

```bash
dotnet build src/Controls/samples/RuntimeXamlTemplateRootRetentionRepro/RuntimeXamlTemplateRootRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -m:1 -nr:false
dotnet run --project src/Controls/samples/RuntimeXamlTemplateRootRetentionRepro/RuntimeXamlTemplateRootRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -- --results=/tmp/runtime-xaml-template-root-retention.txt
cat /tmp/runtime-xaml-template-root-retention.txt
```
