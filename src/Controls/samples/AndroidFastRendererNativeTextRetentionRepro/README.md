# Android FastRenderer native text retention repro

This repro exercises obsolete Android compatibility FastRenderers for `Label` and `Button`. Fast `LabelRenderer.UpdateText()` copies `Label.Text` into the native `TextView.Text`, and fast `ButtonRenderer` routes `Button.Text` through `ButtonLayoutManager.UpdateTextAndImage()` into `AppCompatButton.Text`. Disposal clears listeners and helper objects but does not clear those native text slots.

The app creates 512 fast label renderer cycles and 512 fast button renderer cycles per scenario with 16 KiB generated text values, retains the native peers by JNI global reference, clears the known C114 private element roots in both runs, and compares current disposal against a control run that clears only native `Text` before disposal.
