# iOS PageViewController Context Retention Repro

This Mac Catalyst repro exercises the current iOS/Mac Catalyst `PageHandler` controller path. `PageHandler.CreatePlatformView()` creates a `PageViewController`, and `PageViewController` inherits `ContainerViewController.Context`, a strong `IMauiContext` property. `ContentViewHandler.DisconnectHandler()` clears the platform content view, but it does not clear the retained native controller's context.

The autorun scenario creates 96 real `ContentPage` handlers. Each cycle uses a fresh synthetic window-scoped `MauiContext` backed by a 1 MiB payload service provider, retains only the native `UIViewController` peer, and disconnects the handler. The control path explicitly clears `ContainerViewController.Context`; the current MAUI path leaves the controller context assigned after disconnect.

Run:

```bash
dotnet run --project src/Controls/samples/IosPageViewControllerContextRetentionRepro/IosPageViewControllerContextRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
```

Results are written to `/tmp/ios-pageviewcontroller-context-retention-results.txt`.
