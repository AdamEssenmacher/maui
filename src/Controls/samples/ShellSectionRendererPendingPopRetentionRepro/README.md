# ShellSectionRenderer pending pop retention repro

This Mac Catalyst repro targets `Microsoft.Maui.Controls.Platform.Compatibility.ShellSectionRenderer`.

`ShellSectionRenderer` completes `_popCompletionTask` from `ViewDidDisappear()`, `DidShowViewController()`, or the More tab completion helper. `Dispose()` does not complete or clear `_popCompletionTask`. If a renderer is disposed while a Shell pop completion is pending and that disposed renderer remains rooted by native/controller state, the incomplete pop task can retain the popped page even after `ShellSection.SendPopping()` has removed it from the logical navigation stack.

The sample runs two scenarios with 80 pending Shell pop operations and 1 MiB payloads:

- control: complete and clear `_popCompletionTask` before disposing the renderer
- current: dispose the renderer while `_popCompletionTask` is still assigned and incomplete

The result file is written to `Path.GetTempPath()/shellsectionrenderer-pending-pop-retention-results.txt`, and the app prints the exact path in the report.
