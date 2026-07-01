# Android legacy SwipeViewRenderer action-button image retention repro

This repro exercises obsolete Android compatibility `SwipeViewRenderer`. Its `CreateSwipeItem()` path loads `SwipeItem.IconImageSource` and assigns the drawable to a native action `AppCompatButton` compound drawable slot. Disposal removes and disposes the action view but does not clear the native drawable slot first.

The app creates 96 renderer cycles per scenario, materializes one real legacy swipe action button per renderer, retains the native button peers by JNI global reference, and compares current disposal against a control run that clears only native compound drawables before disposal.
