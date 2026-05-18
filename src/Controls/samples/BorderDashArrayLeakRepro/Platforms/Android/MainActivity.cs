using Android.App;
using Android.Content.PM;
using Android.Runtime;
using Microsoft.Maui;

namespace BorderDashArrayLeakRepro.Platform;

[Activity(
	Theme = "@style/Maui.SplashTheme",
	MainLauncher = true,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode)]
[Register("com.microsoft.maui.borderdasharrayleakrepro.MainActivity")]
public class MainActivity : MauiAppCompatActivity
{
}
