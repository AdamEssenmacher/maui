# Android native text slot retention repro

This repro demonstrates that retained Android native text peers keep large text and hint values assigned after MAUI handler disconnect.

It compares the current MAUI disconnect behavior against a control run that explicitly clears native `TextView` / `EditText` text and hint slots before disconnecting the handlers. The sample autoruns on launch, writes the result to `autorun-results.txt`, and exits.
