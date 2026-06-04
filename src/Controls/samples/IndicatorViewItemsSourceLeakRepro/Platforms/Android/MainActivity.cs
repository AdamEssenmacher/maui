using Android.App;
using Android.Content.PM;
using Android.OS;
using Microsoft.Maui;

namespace IndicatorViewItemsSourceLeakRepro;

[Activity(
	Name = "com.microsoft.maui.indicatorviewitemsourceleakrepro.MainActivity",
	Label = "IndicatorViewItemsSourceLeakRepro",
	Theme = "@style/Maui.SplashTheme",
	MainLauncher = true,
	ConfigurationChanges =
		ConfigChanges.ScreenSize |
		ConfigChanges.Orientation |
		ConfigChanges.ScreenLayout |
		ConfigChanges.UiMode |
		ConfigChanges.SmallestScreenSize |
		ConfigChanges.KeyboardHidden |
		ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		if (Intent?.GetBooleanExtra("autorun", false) == true)
			System.Environment.SetEnvironmentVariable("INDICATOR_REPRO_AUTORUN", "1");

		base.OnCreate(savedInstanceState);
	}
}
