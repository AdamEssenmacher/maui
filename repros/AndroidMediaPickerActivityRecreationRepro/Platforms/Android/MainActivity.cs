using Android.App;
using Android.Content.PM;
using Android.Runtime;
using Microsoft.Maui;

namespace AndroidMediaPickerActivityRecreationRepro.Platforms.Android;

[Activity(
	Label = "MediaPicker Activity Recreation Repro",
	Theme = "@style/Maui.SplashTheme",
	MainLauncher = true,
	LaunchMode = LaunchMode.SingleTop,
	ConfigurationChanges = ConfigChanges.ScreenSize
		| ConfigChanges.Orientation
		| ConfigChanges.ScreenLayout
		| ConfigChanges.UiMode
		| ConfigChanges.SmallestScreenSize
		| ConfigChanges.KeyboardHidden
		| ConfigChanges.Density)]
[Register("com.microsoft.maui.repros.mediapickerrecreation.MainActivity")]
public sealed class MainActivity : MauiAppCompatActivity
{
}
