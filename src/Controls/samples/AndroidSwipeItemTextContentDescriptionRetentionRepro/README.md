# Android SwipeItem Text ContentDescription Retention Repro

This repro isolates Android `SwipeItemMenuItemHandler` native string slots.

The handler copies `SwipeItem.Text` into the native `TextView.Text` slot and
copies `SwipeItem.AutomationId` into `ContentDescription` during native view
creation. `DisconnectHandler()` removes the attach listener only; it does not
clear either native string slot. This sample intentionally does not set an icon,
so the existing drawable-retention repro is not part of the proof.

The app autoruns, writes `autorun-results.txt` under app data, and exits.
