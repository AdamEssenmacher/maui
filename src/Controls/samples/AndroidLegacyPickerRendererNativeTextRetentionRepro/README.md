# Android legacy PickerRenderer native text retention repro

This sample proves that obsolete Android compatibility `PickerRenderer` leaves generated selected-item text and title hint text assigned to its child native `AppCompatEditText` after renderer disposal.

The repro retains child native `AppCompatEditText` peers through JNI global references in both scenarios and compares current MAUI disposal against an explicit native `EditText.Text`/`EditText.Hint` clear control.
