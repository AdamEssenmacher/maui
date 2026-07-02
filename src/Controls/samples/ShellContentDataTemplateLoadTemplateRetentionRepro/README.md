# ShellContent DataTemplate LoadTemplate Retention Repro

This sample proves a managed retention path in `ShellContent.GetOrCreateContent()` for shared type-based `DataTemplate` instances.

`ShellContent.GetOrCreateContent()` rewrites `DataTemplate.LoadTemplate` with a closure that captures the current `ShellContent`. If the same `DataTemplate` instance is kept alive by app resources, a route registry, or another long-lived owner, that rewritten factory keeps the discarded `ShellContent` alive. The retained `ShellContent` can then retain its binding context and cached page.

The repro keeps 80 shared `DataTemplate(typeof(PayloadPage))` instances alive. Each template is used once by a short-lived `ShellContent` with a 1 MiB payload. The app compares:

- a control scenario that resets `LoadTemplate` to a non-capturing factory after `GetOrCreateContent()`;
- current MAUI behavior, which leaves the framework-created closure installed on the shared template.

Expected proving signal:

- the control scenario collects the discarded `ShellContent`, page, payload, and payload buffer instances;
- current MAUI behavior retains nearly all discarded `ShellContent` graphs while only the shared templates remain rooted.

Run on Mac Catalyst:

```bash
dotnet run --project src/Controls/samples/ShellContentDataTemplateLoadTemplateRetentionRepro/ShellContentDataTemplateLoadTemplateRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -- --results=/tmp/shellcontent-datatemplate-loadtemplate-retention.txt
```
