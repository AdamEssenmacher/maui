using Android.App;
using Android.OS;
using Microsoft.Maui;

namespace AndroidFlyoutViewInsetListenerRetentionRepro;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, Exported = true)]
public sealed class MainActivity : MauiAppCompatActivity
{
	public static MainActivity? Current { get; private set; }

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		Current = this;
		base.OnCreate(savedInstanceState);
	}

	protected override void OnDestroy()
	{
		if (Current == this)
			Current = null;

		base.OnDestroy();
	}
}
