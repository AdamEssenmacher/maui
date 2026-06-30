# Android legacy RadioButtonRenderer native text retention repro

This repro exercises the obsolete Android compatibility `RadioButtonRenderer` path. `RadioButtonRenderer.UpdateContent()` copies `RadioButton.ContentAsString()` into `AppCompatRadioButton.Text`, while `RadioButtonRenderer.Dispose(bool)` removes listeners and clears renderer bookkeeping without clearing that native text slot.

The app creates 1,024 renderer cycles with 16 KiB generated radio-button content labels, retains the native `AppCompatRadioButton` peers by JNI global reference, clears the known C115 `Element` root in both runs, and compares current disposal against a control run that clears only the native `Text` slot before disposal.
