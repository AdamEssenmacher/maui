using Android.App;
using Android.Content.PM;
using Android.Runtime;

namespace Maui.Controls.HybridWebViewRepro.Platform;

[Activity(
	Theme = "@style/Maui.SplashTheme",
	MainLauncher = true,
	LaunchMode = LaunchMode.SingleTop,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[Register("com.microsoft.maui.hybridwebviewrepro.MainActivity")]
public class MainActivity : MauiAppCompatActivity
{
}
