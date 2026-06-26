# AppActions OnAppAction initializer leak repro

This sample demonstrates that repeatedly building MAUI hosts with
`ConfigureEssentials(essentials => essentials.OnAppAction(...))` can retain
disposed host state through the static `AppActions.OnAppAction` event.

`EssentialsInitializer.Initialize()` subscribes to `AppActions.OnAppAction` with
an instance method and never unsubscribes. The initializer stores the
`EssentialsBuilder`, which stores app-action handlers registered by
`ConfigureEssentials`. If those handlers capture host-scoped state, disposing the
`MauiApp` does not release the captured state.

The app runs two scenarios:

1. Control: build and dispose throwaway `MauiApp` instances without
   `ConfigureEssentials`.
2. Leak: build and dispose throwaway `MauiApp` instances that configure an
   app-action handler capturing a realistic host payload.

## Mac Catalyst autorun

```bash
dotnet build src/Controls/samples/AppActionsOnAppActionLeakRepro/AppActionsOnAppActionLeakRepro.csproj -f net10.0-maccatalyst
APP_ACTIONS_ON_APP_ACTION_LEAK_REPRO_AUTORUN=1 \
APP_ACTIONS_ON_APP_ACTION_LEAK_REPRO_RESULTS=/private/tmp/appactionsonappactionleakrepro-results.txt \
artifacts/bin/AppActionsOnAppActionLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/AppActionsOnAppActionLeakRepro.app/Contents/MacOS/AppActionsOnAppActionLeakRepro \
  --auto-run \
  --results=/private/tmp/appactionsonappactionleakrepro-results.txt
```

If the sandbox blocks `/private/tmp`, the app writes the report to:

`~/Library/Containers/com.microsoft.maui.appactionsonappactionleakrepro/Data/Documents/AppActionsOnAppActionLeakRepro/autorun-results.txt`

## Observed result

Mac Catalyst autorun on 2026-06-25 used 60 throwaway `MauiApp`
instances/run and 2 MB payload/app.

Control without `ConfigureEssentials` app-action handlers:

- Retained `MauiApp` instances: `0/60`
- Retained app-action payloads: `0/60`

Leaky `ConfigureEssentials(...OnAppAction...)` handlers:

- Retained `MauiApp` instances: `0/60`
- Retained app-action payloads: `60/60`
- Retained payload: `120.0 MB`
- Managed heap delta after GC: `120.5 MB`
- GC heap delta after GC: `121.4 MB`
