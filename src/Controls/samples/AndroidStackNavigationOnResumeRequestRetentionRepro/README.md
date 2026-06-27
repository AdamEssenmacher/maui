# Android StackNavigation OnResume Request Retention Leak Repro

This repro demonstrates that Android `StackNavigationManager.Disconnect()` leaves `OnResumeRequestedArgs` assigned after the delayed-navigation path used when `FragmentManager.IsStateSaved` is hit.

The app keeps disconnected `StackNavigationManager` instances alive in both runs. The control clears `OnResumeRequestedArgs` after disconnect; the current MAUI path does not. Each pending `NavigationRequest` holds a realistic navigation target graph with a touched 1 MiB payload buffer.

Run:

```sh
dotnet build src/Controls/samples/AndroidStackNavigationOnResumeRequestRetentionRepro/AndroidStackNavigationOnResumeRequestRetentionRepro.csproj -f net10.0-android -p:UseMaui=false -p:IncludeAndroidTargetFrameworks=true -p:EmbedAssembliesIntoApk=true -m:1 -nr:false -t:SignAndroidPackage
adb install -r --no-incremental artifacts/bin/AndroidStackNavigationOnResumeRequestRetentionRepro/Debug/net10.0-android/com.microsoft.maui.androidstacknavigationonresumerequestretentionrepro-Signed.apk
adb shell am start -n com.microsoft.maui.androidstacknavigationonresumerequestretentionrepro/crc64101460759c0fa3fb.MainActivity
adb shell run-as com.microsoft.maui.androidstacknavigationonresumerequestretentionrepro cat files/autorun-results.txt
```
