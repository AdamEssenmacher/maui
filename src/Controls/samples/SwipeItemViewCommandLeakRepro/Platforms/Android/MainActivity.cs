using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;

namespace SwipeItemViewCommandLeakRepro.Platforms.Android;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[Register("com.microsoft.maui.swipeitemviewcommandleakrepro.MainActivity")]
public sealed class MainActivity : MauiAppCompatActivity
{
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
	}
}
