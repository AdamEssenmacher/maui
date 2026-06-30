# Android legacy SwipeViewRenderer action-button native text retention repro

This repro exercises obsolete Android compatibility `SwipeViewRenderer`. Its `CreateSwipeItem()` path copies `SwipeItem.Text` into a native action `AppCompatButton.Text` and copies `SwipeItem.AutomationId` into `ContentDescription`. Disposal removes and disposes the action view but does not clear the native string slots first.

The app creates 1,024 renderer cycles per scenario, materializes one real legacy swipe action button per renderer, retains the native button peers by JNI global reference, and compares current disposal against a control run that clears only native `Text` and `ContentDescription` before disposal.
