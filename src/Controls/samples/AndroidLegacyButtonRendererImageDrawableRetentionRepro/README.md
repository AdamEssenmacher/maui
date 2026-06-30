# Android legacy ButtonRenderer image drawable retention repro

This repro exercises the obsolete Android compatibility `ButtonRenderer` path. `ButtonLayoutManager.UpdateImage()` loads `Button.ImageSource` and assigns the result into the native `AppCompatButton` compound drawable slots, while `ButtonRenderer.Dispose(bool)` removes listeners and disposes the layout manager without clearing those native drawable slots.

The app creates 96 renderer cycles with 512x512 generated button images, retains the native `AppCompatButton` peers by JNI global reference, and compares current disposal against a control run that clears only the native compound drawable slots before disposing the same renderer.
