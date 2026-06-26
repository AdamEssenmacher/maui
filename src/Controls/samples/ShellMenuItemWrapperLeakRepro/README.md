# ShellMenuItemWrapperLeakRepro

This repro demonstrates that a long-lived `MenuItem` can retain `MenuShellItem` wrappers through `MenuItem.PropertyChanged`.

`MenuShellItem` subscribes to `MenuItem.PropertyChanged` in its constructor and has no detach path. When a `MenuItem` is reused as the source for transient Shell flyout wrappers, the source `MenuItem` can retain each wrapper and any payload still attached to it.

Run the autorun proof:

```sh
dotnet build src/Controls/samples/ShellMenuItemWrapperLeakRepro/ShellMenuItemWrapperLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/ShellMenuItemWrapperLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/ShellMenuItemWrapperLeakRepro.app --args --auto-run --results=/tmp/shellmenuitemwrapperleakrepro-results.txt
cat /tmp/shellmenuitemwrapperleakrepro-results.txt
```

Expected result:

```text
Run: control: dropped ordinary ShellItem wrappers
  wrappers alive after full GC: 0/80
  payloads alive after full GC: 0/80

Run: leak: long-lived MenuItem roots MenuShellItem wrapper
  wrappers alive after full GC: 80/80
  payloads alive after full GC: 80/80
  retained payload bytes: 80.0 MiB (100.0%)
```
