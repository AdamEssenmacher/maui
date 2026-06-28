# IosButtonRendererBackgroundPatternRetentionRepro

This Mac Catalyst repro checks whether legacy iOS `ButtonRenderer` leaves generated brush
background pattern images alive on retained native `UIButton` peers after renderer disposal.

The control scenario clears `UIButton.BackgroundColor` before disposal. The current MAUI
scenario leaves the pattern color assigned and verifies the `Button`, brush, payload, and
renderer collect while retained native button peers still hold their background color state.
