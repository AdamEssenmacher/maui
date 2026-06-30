# Android legacy DatePickerRenderer/TimePickerRenderer native text retention repro

This repro exercises obsolete Android compatibility `DatePickerRenderer` and `TimePickerRenderer`. Both renderers format the selected value into a native `PickerEditText.Text` slot, and disposal cleans dialog/listener state but does not clear that native text.

The app creates 1,024 date picker renderer cycles and 1,024 time picker renderer cycles per scenario with 4 KiB generated literal format strings, retains the native edit-text peers by JNI global reference, and compares current disposal against a control run that clears only native `Text` before disposal.
