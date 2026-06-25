# Shell canceled PushAsync implicit route leak repro

This sample demonstrates that canceling `Shell.Current.Navigation.PushAsync(page)` can retain the never-displayed page through Shell's static implicit route table.

`ShellNavigationManager.GoToAsync` registers `PagePushing` with `Routing.RegisterImplicitPageRoute` before navigation cancellation is resolved. If `Shell.Navigating` cancels the push, `GoToAsync` returns early and does not call `Routing.ClearImplicitPageRoutes`. The static `Routing.s_implicitPageRoutes` dictionary can therefore keep the canceled page alive.

The app runs three scenarios:

1. Control: create pages without calling Shell push.
2. Leak: call `Shell.Current.Navigation.PushAsync(page)` while `Shell.Navigating` cancels each push.
3. Cleanup: run canceled pushes, then perform a successful Shell navigation to trigger implicit route cleanup.

## Mac Catalyst autorun

```bash
dotnet build src/Controls/samples/ShellCanceledPushLeakRepro/ShellCanceledPushLeakRepro.csproj -f net10.0-maccatalyst
open -W artifacts/bin/ShellCanceledPushLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ShellCanceledPushLeakRepro.app --args \
  --auto-run \
  --results=/private/tmp/shellcanceledpushleakrepro-results.txt
```

If the sandbox blocks `/private/tmp`, the app writes the report to:

`~/Library/Containers/com.microsoft.maui.shellcanceledpushleakrepro/Data/Documents/ShellCanceledPushLeakRepro/autorun-results.txt`

Observed Mac Catalyst autorun result on 2026-06-25:

- Control retained `0/60` pages, `0/60` root layouts, and `0/60` payload view models.
- Canceled `Shell.Current.Navigation.PushAsync(page)` retained `60/60` pages, `60/60` root layouts, `60/60` payload view models, and `120.0 MB` of payload.
- Cleanup by a successful Shell navigation retained `0/60` pages, `0/60` root layouts, and `0/60` payload view models.
