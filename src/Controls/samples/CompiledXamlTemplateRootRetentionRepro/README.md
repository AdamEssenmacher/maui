# Compiled XAML DataTemplate Root Retention Repro

This sample proves a managed retention path in compiled XAML-created `DataTemplate` instances. The project forces XamlC with `[assembly: XamlCompilation(XamlCompilationOptions.Compile)]` so it exercises the build-time template factory path.

`XamlC.SetPropertiesVisitor.SetDataTemplate(...)` creates a generated template-factory target that stores the XAML root object in a `root` field. If app code extracts a page-local compiled-XAML `DataTemplate` and keeps it in a registry, plugin cache, design surface, or deferred view factory, the escaped template can keep the discarded XAML root page alive.

The repro keeps 80 `DataTemplate` instances extracted from a compiled XAML `ContentPage`. Each page has a 1 MiB binding-context payload. The app compares:

- a control scenario that resets `LoadTemplate` to a non-capturing factory after the template is extracted;
- the current MAUI behavior, where the XamlC-generated `LoadTemplate` target remains installed.

The only intentionally retained objects are the extracted templates. A proven run retains the current-behavior pages, payloads, and payload buffers while the control run collects them.

## Run

```bash
dotnet build src/Controls/samples/CompiledXamlTemplateRootRetentionRepro/CompiledXamlTemplateRootRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -m:1 -nr:false
dotnet run --project src/Controls/samples/CompiledXamlTemplateRootRetentionRepro/CompiledXamlTemplateRootRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -- --results=/tmp/compiled-xaml-template-root-retention.txt
cat /tmp/compiled-xaml-template-root-retention.txt
```
