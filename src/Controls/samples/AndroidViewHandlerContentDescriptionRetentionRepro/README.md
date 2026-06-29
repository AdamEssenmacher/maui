# Android ViewHandler ContentDescription Retention Repro

This repro isolates the generic Android `ViewHandler.MapAutomationId()` path.

`MapAutomationId()` copies `IView.AutomationId` into the native Android
`View.ContentDescription` slot. Android disconnect removes the MAUI
accessibility delegate but does not clear the native content-description value.
The sample uses ordinary current `BoxViewHandler` instances and retains only
the Android native view peers.

The app autoruns, writes `autorun-results.txt` under app data, and exits.
