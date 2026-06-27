# ShellSectionRenderer completion task retention repro

This Mac Catalyst repro targets `Microsoft.Maui.Controls.Platform.Compatibility.ShellSectionRenderer`.

`ShellSectionRenderer` stores pending push/pop-to-root completion sources in `_completionTasks`. `ViewDidDisappear()` completes and clears that dictionary, but `Dispose()` does not. For pop-to-root, `ShellSection.SendPoppingToRoot()` removes the old stack from `_navStack` before awaiting the completion task, so an incomplete task can retain pages that are already logically removed.

The sample runs two scenarios with 40 pending pop-to-root operations. Each operation removes two pages with 1 MiB payloads, for 80 MiB total payload:

- control: complete and clear `_completionTasks` before disposing the renderer
- current: dispose the renderer while `_completionTasks` still contains incomplete completion sources

The result file is written to `Path.GetTempPath()/shellsectionrenderer-completiontasks-retention-results.txt`, and the app prints the exact path in the report.
