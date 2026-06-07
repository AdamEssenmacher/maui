using Android.App;
using Android.Content.PM;
using Microsoft.Maui;

namespace ShapeCollectionResetLeakRepro;

[Activity(
	Label = "ShapeCollectionResetLeakRepro",
	Theme = "@style/Maui.SplashTheme",
	MainLauncher = true,
	ConfigurationChanges = ConfigChanges.ScreenSize
		| ConfigChanges.Orientation
		| ConfigChanges.ScreenLayout
		| ConfigChanges.UiMode
		| ConfigChanges.SmallestScreenSize
		| ConfigChanges.KeyboardHidden
		| ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
