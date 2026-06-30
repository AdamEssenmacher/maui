# Android SwitchCell Native Text Retention Repro

This repro exercises the legacy Android `SwitchCellRenderer` path and keeps only
native `SwitchCellView` row peers alive after renderer disconnect.

`SwitchCellRenderer.UpdateText()` copies `SwitchCell.Text` into
`BaseCellView.MainText`, which stores the value in the private `_mainTextText`
field and assigns it to the child main `TextView.Text` slot. Renderer disconnect
does not clear either native row text slot.

Both runs neutralize the known cell roots from C107/C274 by clearing
`BaseCellView._cell`, `SwitchCellView.Cell`, and `ContentDescription`. The
control run also clears `_mainTextText` and the child main `TextView.Text`;
current MAUI leaves them assigned. The sample creates 512 rows with a 16 KiB
generated switch label, demonstrating roughly 32 MiB of retained text state when
the native row peers survive.

Run with:

```sh
dotnet build src/Controls/samples/AndroidSwitchCellNativeTextRetentionRepro/AndroidSwitchCellNativeTextRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:EmbedAssembliesIntoApk=true -p:AndroidFastDeploymentType= -m:1 -nr:false -v:minimal -clp:Summary
```

The app writes its autorun result to `files/autorun-results.txt`.
