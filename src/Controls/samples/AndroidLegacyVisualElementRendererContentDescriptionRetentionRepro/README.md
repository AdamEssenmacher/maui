# Android legacy VisualElementRenderer ContentDescription retention repro

This sample proves that obsolete Android compatibility `VisualElementRenderer<T>` peers leave generated automation/accessibility text assigned to native `View.ContentDescription` after renderer disposal.

The repro retains disposed `BoxRenderer` peers in both scenarios, clears the known `MotionEventHelper._element` root in both scenarios, and compares current MAUI disposal against an explicit native `ContentDescription` clear control.
