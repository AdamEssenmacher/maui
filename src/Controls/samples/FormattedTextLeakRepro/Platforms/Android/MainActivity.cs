using Android.App;
using Android.Content.PM;
using Android.Runtime;

namespace FormattedTextLeakRepro;

[Activity(
	Theme = "@style/Maui.SplashTheme",
	MainLauncher = true,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode)]
[Register("com.microsoft.maui.formattedtextleakrepro.MainActivity")]
public class MainActivity : MauiAppCompatActivity
{
}
