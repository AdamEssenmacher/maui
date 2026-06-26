# Layout child handler disconnect leak repro

This repro demonstrates that iOS/Mac Catalyst `LayoutHandler` removes an old native layout child when `Layout.Children.Remove(...)` runs, but it does not disconnect the removed child view handler.

The control scenario keeps removed layout children alive after the same removal plus an explicit child `DisconnectHandler()`. The current-handler scenario keeps the same kind of removed layout children alive after the existing `ILayoutHandler.Remove` command path. Each child handler owns a 1 MiB payload so retained handlers show a visible impact.
