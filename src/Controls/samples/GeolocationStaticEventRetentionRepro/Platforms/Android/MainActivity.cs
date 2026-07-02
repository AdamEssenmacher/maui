using Android.App;
using Android.OS;
using Microsoft.Maui;

namespace GeolocationStaticEventRetentionRepro;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, Exported = true)]
public sealed class MainActivity : MauiAppCompatActivity
{
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
	}
}
