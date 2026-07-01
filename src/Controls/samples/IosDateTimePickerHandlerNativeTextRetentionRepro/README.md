# iOS DatePicker/TimePicker Native Text Retention Repro

This repro exercises current iOS `DatePickerHandler` and `TimePickerHandler` display-text cleanup. The handlers copy formatted date/time values into retained native `MauiDatePicker.Text` and `MauiTimePicker.Text` fields, then disconnect without clearing those native slots.

The workload creates 128 disposed `DatePicker` handlers and 128 disposed `TimePicker` handlers, each with an 8 KiB formatted display value. That models repeated scheduling/dispatch screens carrying large localized date/time context and keeps the native-text payload near 2 MiB before native view overhead.

The control path keeps the same retained native peers alive but explicitly clears only native `Text`/`AttributedText` before handler disconnect. A proven run reports `RESULT: PROVEN`.
