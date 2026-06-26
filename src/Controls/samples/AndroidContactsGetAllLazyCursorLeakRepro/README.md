# Android Contacts.GetAllAsync Lazy Cursor Leak Repro

This repro exercises the real Android `Contacts.Default.GetAllAsync()` path and inspects the native `ICursor` captured by the returned lazy enumerable.

The control fully enumerates every returned sequence and verifies the captured cursor closes. The current paths retain returned enumerables after either never enumerating them or disposing the enumerator after the first row; both paths leave the captured native cursor open.

Before launching the app, grant contacts permissions:

```bash
adb shell pm grant com.microsoft.maui.androidcontactsgetalllazycursorleakrepro android.permission.READ_CONTACTS
adb shell pm grant com.microsoft.maui.androidcontactsgetalllazycursorleakrepro android.permission.WRITE_CONTACTS
```
