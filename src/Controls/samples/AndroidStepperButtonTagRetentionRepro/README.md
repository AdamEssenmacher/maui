# Android Stepper Button Tag Retention Repro

This repro proves that retained Android `Stepper` native buttons keep disconnected `StepperHandler` instances and their `MauiContext` graphs alive through `StepperHandlerHolder` objects stored in the buttons' `Tag` properties.

It creates transient `StepperHandler` instances with payload-bearing per-cycle `MauiContext` service providers, retains only the native plus/minus buttons, then compares current disconnect behavior with explicit native button `Tag` and click-listener clearing.

The app writes its autorun result to `files/autorun-results.txt`.
