# IosCompatImageRendererNativeImageRetentionRepro

This Mac Catalyst repro proves that legacy iOS compatibility `ImageRenderer`
and `ImageButtonRenderer` leave assigned native image slots alive
when their retained native peers survive renderer disposal.

The control scenario clears the native image slots before disposal. The current MAUI
scenario leaves them assigned and verifies the virtual views and image sources collect.
