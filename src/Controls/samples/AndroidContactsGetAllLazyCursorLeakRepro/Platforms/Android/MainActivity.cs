using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Microsoft.Maui;

namespace AndroidContactsGetAllLazyCursorLeakRepro;

[Activity(
	Theme = "@style/Maui.SplashTheme",
	MainLauncher = true,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[Register("com.microsoft.maui.androidcontactsgetalllazycursorleakrepro.MainActivity")]
public sealed class MainActivity : MauiAppCompatActivity
{
	static int s_started;

	protected override void OnResume()
	{
		base.OnResume();

		if (Interlocked.Exchange(ref s_started, 1) == 0)
			_ = ReproRunner.RunAsync(this);
	}
}
