# Android legacy LabelRenderer native text retention repro

This sample proves that obsolete Android compatibility `LabelRenderer` leaves generated label text assigned to its child native `TextView.Text` after renderer disposal.

The repro retains child native `TextView` peers through JNI global references in both scenarios, clears the known `MotionEventHelper._element` root in both scenarios, and compares current MAUI disposal against an explicit native text clear control.
