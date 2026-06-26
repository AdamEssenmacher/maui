# AndroidDragDropLocalStateRetentionLeakRepro

This repro demonstrates an Android drag/drop local-state retention path in `GesturePlatformManager`.

`GesturePlatformManager.Dispose()` disposes tap/pan and scale detectors, but it does not dispose the lazily created `DragAndDropGestureHandler`. `SetupElement(old, null)` also leaves the native `View.SetOnDragListener(...)` registration alone because it nulls `_handler` before `UpdateDragAndDrop()` can clear the listener. If a drag operation is interrupted before `DragAction.Ended`, `DragAndDropGestureHandler._currentCustomLocalStateData` can retain the source view and `DataPackage` payload after the gesture manager is disposed.

The autorun compares the current disposal path with a control path that disposes the drag/drop handler before disposing the owning gesture manager.
