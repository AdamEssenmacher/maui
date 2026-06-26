# KeyboardAutoManagerDisconnectLeakRepro

This repro demonstrates that `KeyboardAutoManagerScroll.Disconnect()` can retain the last active editor if it is called after a begin-editing notification and before the keyboard hide path clears static state.

`KeyboardAutoManagerScroll.Connect()` stores observers for UIKit editing and keyboard notifications. `DidUITextBeginEditing` stores the active editor in static fields such as `View`. `Disconnect()` removes the observers but does not clear those fields, so the later keyboard hide notifications cannot clear the last editor graph.

Observed Mac Catalyst result:

```text
KeyboardAutoManagerDisconnectLeakRepro
Attempts: 12
Payload per editor: 16 MiB
Leak proved: True

Run: control: begin-editing followed by normal keyboard hide
  tracked editors: 12
  text fields alive after full GC: 0/12
  editor payloads alive after full GC: 0/12
  retained payload bytes: 0 B (0.0%)

Run: leak: Disconnect while an editor is active
  tracked editors: 12
  text fields alive after full GC: 1/12
  editor payloads alive after full GC: 1/12
  retained payload bytes: 16.0 MiB (8.3%)
```

```sh
dotnet build src/Controls/samples/KeyboardAutoManagerDisconnectLeakRepro/KeyboardAutoManagerDisconnectLeakRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false
open -W artifacts/bin/KeyboardAutoManagerDisconnectLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/KeyboardAutoManagerDisconnectLeakRepro.app --args --auto-run --results=/tmp/keyboardautomanagerdisconnectleakrepro-results.txt
cat /tmp/keyboardautomanagerdisconnectleakrepro-results.txt
```
