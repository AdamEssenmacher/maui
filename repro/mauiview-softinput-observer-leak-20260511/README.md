# MauiView SoftInput Observer Leak Repro

This repro targets a suspected leak in `src/Core/src/Platform/iOS/MauiView.cs`.

`MauiView` subscribes to `UIKeyboard.WillShowNotification` and `UIKeyboard.WillHideNotification` when its virtual view has `SafeAreaEdges` containing `SoftInput`. When the native view is removed from the window, `MovedToWindow()` calls `UpdateKeyboardSubscription()`, but that method only unsubscribes inside the `Window != null` branch. The notification observer can therefore keep the detached native view alive.

## Run

From the repository root:

```bash
./.dotnet/dotnet build repro/mauiview-softinput-observer-leak-20260511/MauiSoftInputObserverLeakRepro.csproj -f net10.0-maccatalyst -t:Run
```

If the local Xcode is newer than the pinned workload expects, add:

```bash
-p:ValidateXcodeVersion=false
```

To capture stdout directly, build first and then run the generated executable:

```bash
./.dotnet/dotnet build repro/mauiview-softinput-observer-leak-20260511/MauiSoftInputObserverLeakRepro.csproj -f net10.0-maccatalyst -p:ValidateXcodeVersion=false
"artifacts/bin/MauiSoftInputObserverLeakRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/MauiSoftInputObserverLeakRepro.app/Contents/MacOS/MauiSoftInputObserverLeakRepro"
```

The app runs automatically and exits with:

- `0` when the leak is reproduced.
- `2` when the suspect is not proven.
- `3` when the harness fails.

It writes the detailed result to the app data directory and prints the path on screen and to stdout.

## Expected Current Result

The control scenario (`SafeAreaEdges.None`) should collect all tracked platform views after forced GC.

The suspect scenario (`SafeAreaEdges.SoftInput`) should retain one or more tracked platform views after forced GC. That retained native view is the proof surface: the virtual view and handler can collect, but MAUI-owned keyboard notification observers still retain the platform `MauiView`.

Verified on MacCatalyst from this checkout on May 11, 2026:

```text
RESULT: LEAK REPRODUCED
Control SafeAreaEdges.None: virtual=0/12, handler=0/12, platform=0/12
Suspect SafeAreaEdges.SoftInput: virtual=0/12, handler=0/12, platform=12/12
Result file: /Users/adam/Library/softinput-observer-leak-result.txt
```

## GitHub Issue Check

Open issue searches on May 11, 2026 did not find an open `dotnet/maui` issue tracking this memory leak pattern:

- `MauiView keyboard notification leak SafeAreaEdges SoftInput`
- `NSNotificationCenter MauiView leak`
- `SafeAreaEdges SoftInput iOS leak`
- `keyboard notification observer iOS memory leak`
- `MauiView SafeAreaEdges`
- `SafeAreaEdges.SoftInput memory leak`
- `MauiView keyboard leak`
- `SoftInput platform alive`
- `UIKeyboard.WillShowNotification`

Related open issues found were SafeArea layout behavior issues, not memory leaks.
