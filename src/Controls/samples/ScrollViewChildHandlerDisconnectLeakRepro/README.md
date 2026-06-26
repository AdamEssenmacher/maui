# ScrollView child handler disconnect leak repro

This repro demonstrates that iOS/Mac Catalyst `ScrollViewHandler` removes the old native content view when `ScrollView.Content` is cleared or replaced, but it does not disconnect the old child view handler.

The control scenario keeps removed scroll content views alive after explicitly calling `DisconnectHandler()` on their child handlers. The current-handler scenario keeps the same kind of removed content views alive after `Content = null` and the existing mapper update. Each child handler owns a 1 MiB payload so retained handlers show a visible impact.
