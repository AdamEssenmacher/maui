# XAML NameScope Removed Child Retention Repro

This sample proves a managed retention path where a live runtime-XAML root keeps removed `x:Name` children alive through its attached `NameScope`.

`NameScope.RegisterName` stores both names and values strongly, and normal layout removal does not call `UnregisterName`. The repro uses runtime `LoadFromXaml(string)` so there are no generated code-behind fields for the named child. It keeps 120 root pages alive, removes one named child from each page, and gives each removed child a 1 MiB payload as its direct `BindingContext`.

The app compares:

- a control scenario that calls `UnregisterName("RemovedChild")` after removing the named child;
- current MAUI behavior, where the root page's `NameScope` still stores the removed child.

A proven run retains the current-behavior removed children and payload buffers while the control run collects them. The retained graph is:

```text
Retained runtime XAML root page -> NameScope -> x:Name removed child -> BindingContext payload
```

## Run

```bash
dotnet build src/Controls/samples/XamlNameScopeRemovedChildRetentionRepro/XamlNameScopeRemovedChildRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false -v:minimal -clp:Summary
open -W "artifacts/bin/XamlNameScopeRemovedChildRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/XAML NameScope Removed Child Retention.app" --args --results=/tmp/xaml-namescope-removed-child-retention-results.txt
cat /tmp/xaml-namescope-removed-child-retention-results.txt
```
