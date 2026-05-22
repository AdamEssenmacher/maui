using Android.App;
using Android.Content.PM;
using Microsoft.Maui;

namespace AndroidWebViewRefreshRepro.Platforms.Android;

[Activity(
	Label = "Android WebView Refresh Repro",
	Theme = "@style/Maui.SplashTheme",
	MainLauncher = true,
	ConfigurationChanges = ConfigChanges.ScreenSize
		| ConfigChanges.Orientation
		| ConfigChanges.ScreenLayout
		| ConfigChanges.UiMode
		| ConfigChanges.SmallestScreenSize
		| ConfigChanges.KeyboardHidden
		| ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
}
