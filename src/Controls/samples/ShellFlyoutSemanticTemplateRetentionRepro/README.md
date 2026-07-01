# Shell Flyout Semantic Template Retention Repro

This sample proves that a long-lived `BaseShellItem` can retain generated default Shell flyout item template cells through the semantic-property propagation helper.

`BaseShellItem.CreateDefaultFlyoutItemCell()` creates a default flyout item `Grid`. When the grid receives a `BaseShellItem` binding context, its `BindingContextChanged` handler calls `SemanticProperties.FakeBindSemanticProperties(source, grid)`. That helper subscribes to `source.PropertyChanged` and returns an `ActionDisposable`, but the template only disposes the token on a later binding-context change.

The current scenario creates generated default flyout cells, assigns long-lived `FlyoutItem` sources as binding contexts, and then abandons the cells without another binding-context change. The control scenario keeps the same long-lived Shell item sources but clears `Grid.BindingContext` before abandonment, which triggers the existing unsubscribe path.

Run:

```bash
dotnet run --project src/Controls/samples/ShellFlyoutSemanticTemplateRetentionRepro/ShellFlyoutSemanticTemplateRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -m:1 -nr:false
```

Result file:

```text
/tmp/shell-flyout-semantic-template-retention-results.txt
```
