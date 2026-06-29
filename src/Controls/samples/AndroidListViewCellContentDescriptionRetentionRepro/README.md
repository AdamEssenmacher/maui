# Android ListView Cell ContentDescription Retention Repro

This repro isolates Android compatibility `TextCellRenderer.UpdateAutomationId()`.

`TextCellRenderer` copies `TextCell.AutomationId` into the native row view's
`ContentDescription`. The known native `_cell` back-reference leak is cleared in
both runs so the only measured difference is whether the retained Android native
row view still has the generated `ContentDescription` assigned.

The app autoruns, writes `autorun-results.txt` under app data, and exits.
