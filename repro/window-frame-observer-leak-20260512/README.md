# WindowHandler Frame Observer Leak Repro

This repro targets a suspected leak in `src/Core/src/Handlers/Window/WindowHandler.iOS.cs`.

`WindowHandler.ConnectHandler()` always connects `FrameObserverProxy`, which observes `UIWindow.frame` via KVO. `WindowHandler.DisconnectHandler()` only disconnects that proxy inside `OperatingSystem.IsMacCatalystVersionAtLeast(16)`. On iOS, and on MacCatalyst below 16, the KVO token remains connected after handler disconnect.

The app runs two scenarios:

- `manual-frame-observer-disconnect`: disconnect the handler, then invoke `FrameObserverProxy.Disconnect()` by reflection. This simulates the expected fixed cleanup path.
- `current-disconnect`: disconnect the handler using the current MAUI code only.

Each scenario keeps the observed `UIWindow` instances alive to match the real application case where platform windows can outlive a MAUI handler. It then mutates `UIWindow.Frame` after handler disconnect and checks whether the private `FrameObserverProxy` and KVO token remain alive after forced GC.

## Run

From the repository root, use an iOS simulator:

```bash
./.dotnet/dotnet build repro/window-frame-observer-leak-20260512/MauiWindowFrameObserverLeakRepro.csproj -f net10.0-ios -t:Run -p:_DeviceName=:v2:udid=<SIMULATOR_UDID> -p:RuntimeIdentifier=iossimulator-arm64 -p:ValidateXcodeVersion=false
```

For example:

```bash
./.dotnet/dotnet build repro/window-frame-observer-leak-20260512/MauiWindowFrameObserverLeakRepro.csproj -f net10.0-ios -t:Run -p:_DeviceName=:v2:udid=4668BAB0-590F-491C-8EF7-41B8044F15A7 -p:RuntimeIdentifier=iossimulator-arm64 -p:ValidateXcodeVersion=false
```

The app runs automatically and exits with:

- `0` when the leak is reproduced.
- `2` when the suspect is not proven.
- `3` when the harness fails.
- `4` when the runtime is not expected to reproduce this path, such as MacCatalyst 16 or newer.

It writes the detailed result to the app data directory and prints the path on screen and to stdout.

## Observed Current Result

On iOS, `manual-frame-observer-disconnect` should produce no post-disconnect frame callbacks and no retained `FrameObserverProxy` or KVO token.

The `current-disconnect` scenario does continue receiving `UIWindow.Frame` KVO callbacks after `WindowHandler.DisconnectHandler()`, and iOS logs `Warning: observer object was not disposed manually with Dispose()`.

However, this harness did not prove a persistent memory leak: the private `FrameObserverProxy` and KVO token both collect after forced GC.

Verified on an iOS 26.4 simulator from this checkout on May 12, 2026:

```text
RESULT: NOT PROVEN
Control: callbacks-after-disconnect=0, proxy-alive=0/20, kvo-token-alive=0/20
Suspect: callbacks-after-disconnect=20, proxy-alive=0/20, kvo-token-alive=0/20
Result file: /Users/adam/Library/Developer/CoreSimulator/Devices/4668BAB0-590F-491C-8EF7-41B8044F15A7/data/Containers/Data/Application/93F58A6B-4251-4A29-A9D4-4EA0D390A95A/Library/window-frame-observer-leak-result.txt
```

The result file also showed `handler-alive=0` and `virtual-window-alive=0` in both scenarios while the platform windows were intentionally retained.

This is still evidence of an undisposed stale observer, but it is weaker than a memory leak repro.
