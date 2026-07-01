# Android Material3 native text slot retention repro

This repro demonstrates that retained Android Material3 native text peers keep large text and hint values assigned after MAUI handler disconnect.

It resolves the public MAUI controls through the Material3 handler registrations and asserts `LabelHandler2`, `EntryHandler2`, `EditorHandler2`, `SearchBarHandler2`, and `RadioButtonHandler2` are used. It then compares the current MAUI disconnect behavior against a control run that explicitly clears native `TextView` and `TextInputLayout` text/hint slots before disconnecting the handlers. The sample autoruns on launch, writes the result to `autorun-results.txt`, and exits.
