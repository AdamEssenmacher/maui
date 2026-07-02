# SourceGen DataTemplate StaticResource Retention Repro

This sample proves a managed retention path in `MauiXamlInflator=SourceGen`-created `DataTemplate` instances when the template body uses a page-level `StaticResource`.

`SourceGen` emits `DataTemplate.LoadTemplate = () => ...` for template content. When a `StaticResource` inside the template cannot be resolved as a template-local resource, SourceGen falls back to runtime markup resolution and creates a `XamlServiceProvider(this)` from inside that generated template lambda. If app code extracts the page-local `DataTemplate` and keeps it in a registry, plugin cache, design surface, or deferred view factory, the escaped template can keep the declaring page and its `BindingContext` alive.

The repro keeps 80 `DataTemplate` instances extracted from a SourceGen-inflated `ContentPage`. Each page has a 1 MiB binding-context payload. The app compares:

- a control scenario that resets `LoadTemplate` to a non-capturing factory after the template is extracted;
- the current MAUI behavior, where the SourceGen-generated `LoadTemplate` lambda remains installed.

The only intentionally retained objects are the extracted templates. A proven run retains the current-behavior pages, payloads, and payload buffers while the control run collects them.

## Run

```bash
dotnet build src/Controls/samples/SourceGenTemplateStaticResourceRetentionRepro/SourceGenTemplateStaticResourceRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -m:1 -nr:false
open -W "artifacts/bin/SourceGenTemplateStaticResourceRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/SourceGen Template StaticResource Retention.app" --args --results=/tmp/sourcegen-template-staticresource-retention-results.txt
cat /tmp/sourcegen-template-staticresource-retention-results.txt
```
