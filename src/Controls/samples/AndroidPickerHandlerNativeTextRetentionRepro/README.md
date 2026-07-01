# Android Picker handler native text retention repro

This repro demonstrates that retained Android picker native peers keep selected item text and title/hint values assigned after MAUI handler disconnect.

It exercises the classic Android `PickerHandler` and the Material3 `PickerHandler2` display field paths without opening picker dialogs, keeping this separate from dialog title/item retention. It compares the current MAUI disconnect behavior against a control run that explicitly clears native `TextView` text/hint slots before disconnecting the handlers. The sample autoruns on launch, writes the result to `autorun-results.txt`, and exits.
