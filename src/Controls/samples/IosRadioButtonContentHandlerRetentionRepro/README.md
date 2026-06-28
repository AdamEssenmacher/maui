# iOS RadioButton content handler retention repro

This repro demonstrates that iOS/Mac Catalyst `RadioButtonHandler` removes old templated/content native children when `RadioButton.Content` is cleared, but it does not disconnect the old child view handler.

The control scenario keeps removed content views alive after explicitly calling `DisconnectHandler()` on their child handlers. The current-handler scenario keeps the same kind of removed content views alive after `Content = null` and the existing mapper update. Each child handler owns a 1 MiB payload so retained handlers show a visible impact.
