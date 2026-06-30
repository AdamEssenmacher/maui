# Android legacy ButtonRenderer native text retention repro

This repro exercises the obsolete Android compatibility `ButtonRenderer` path. `ButtonLayoutManager.UpdateTextAndImage()` copies `Button.Text` into `AppCompatButton.Text`, while `ButtonRenderer.Dispose(bool)` removes listeners and disposes the layout manager without clearing that native text slot.

The app creates 1,024 renderer cycles with 16 KiB generated button labels, retains the native `AppCompatButton` peers by JNI global reference, and compares current disposal against a control run that clears only the native `Text` slot before disposing the same renderer.
