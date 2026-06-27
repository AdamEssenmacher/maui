# Compatibility Forms Static MauiContext Retention Leak Repro

This repro proves that compatibility `Forms.Init(IActivationState)` stores a window-scoped `IMauiContext` in static `Forms.MauiContext`. That context roots its scoped service provider after the window scope is otherwise discarded, retaining scoped service payloads until the static field is replaced or cleared.
