# Android legacy EntryRenderer native text retention repro

This sample proves that obsolete Android compatibility `EntryRenderer` leaves generated entry text and placeholder text assigned to its child native `EditText` after renderer disposal.

The repro retains child native `EditText` peers through JNI global references in both scenarios and compares current MAUI disposal against an explicit native `EditText.Text`/`EditText.Hint` clear control.
