# XAML Transient NameScope Detached Child Retention Repro

This sample proves a managed retention path in XAML-created children that are detached and retained by a longer-lived owner.

XAML inflators assign `Element.transientNamescope` to child elements so `VisualStateManager` can resolve names before parenting. The field is not cleared after the element is parented or detached. Because `NameScope` stores `x:Name` targets strongly, a retained detached child can keep the discarded page root and page-owned payload graph alive through its stale transient name scope.

The project forces XamlC with `[assembly: XamlCompilation(XamlCompilationOptions.Compile)]`. The repro keeps 96 detached named children from XAML pages. Each page owns a 1 MiB payload that is not assigned to `BindingContext`, so the retained child should not naturally own the payload. The app compares:

- a control scenario that clears the detached child's `transientNamescope`;
- the current MAUI behavior, where the stale transient name scope remains installed.

The only intentionally retained objects are the detached child elements. A proven run retains the current-behavior pages, payloads, and payload buffers while the control run collects them.

## Run

```bash
dotnet build src/Controls/samples/XamlTransientNameScopeDetachedChildRetentionRepro/XamlTransientNameScopeDetachedChildRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -m:1 -nr:false
open -W "artifacts/bin/XamlTransientNameScopeDetachedChildRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/XAML Transient NameScope Detached Child Retention.app" --args --results=/tmp/xaml-transient-namescope-detached-child-retention.txt
cat /tmp/xaml-transient-namescope-detached-child-retention.txt
```
