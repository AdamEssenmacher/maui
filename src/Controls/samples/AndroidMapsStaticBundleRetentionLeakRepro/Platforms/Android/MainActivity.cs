using Android.App;
using Microsoft.Maui;

namespace AndroidMapsStaticBundleRetentionLeakRepro;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true)]
public sealed class MainActivity : MauiAppCompatActivity
{
}
