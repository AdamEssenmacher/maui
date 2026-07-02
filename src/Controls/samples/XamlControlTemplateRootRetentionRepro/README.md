# XAML ControlTemplate Root Retention Repro

This sample proves managed retention paths in runtime and compiled XAML-created `ControlTemplate` instances. The project forces XamlC with `[assembly: XamlCompilation(XamlCompilationOptions.Compile)]` so the compiled scenario exercises the build-time template factory path.

Runtime XAML assigns `ElementTemplate.LoadTemplate` to a closure that captures the parser `HydrationContext`, including `RootElement`. Compiled XAML/XamlC creates a generated template-factory target that stores the declaring XAML root in a `root` field. If app code extracts a page-local `ControlTemplate` and keeps it in a registry, plugin cache, design surface, or deferred view factory, the escaped template can keep the discarded page alive.

The repro keeps 80 runtime-XAML `ControlTemplate` instances and 80 compiled-XAML `ControlTemplate` instances. Each source page has a 1 MiB binding-context payload. The app compares each path against a control scenario that resets `LoadTemplate` to a non-capturing factory after extraction.

## Run

```bash
dotnet build src/Controls/samples/XamlControlTemplateRootRetentionRepro/XamlControlTemplateRootRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -m:1 -nr:false
dotnet run --project src/Controls/samples/XamlControlTemplateRootRetentionRepro/XamlControlTemplateRootRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -- --results=/tmp/xaml-controltemplate-root-retention-results.txt
cat /tmp/xaml-controltemplate-root-retention-results.txt
```
