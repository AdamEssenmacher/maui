# Android legacy SearchBarRenderer native text retention repro

This sample proves that obsolete Android compatibility `SearchBarRenderer` leaves generated query text and placeholder text assigned to its internal native `AppCompatAutoCompleteTextView` after renderer disposal.

The repro retains the internal native search edit-text peers through JNI global references in both scenarios and compares current MAUI disposal against an explicit native `EditText.Text`/`EditText.Hint` clear control.
