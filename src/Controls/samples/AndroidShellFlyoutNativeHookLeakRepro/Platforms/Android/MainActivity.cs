using Android.App;
using Microsoft.Maui;

namespace AndroidShellFlyoutNativeHookLeakRepro;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, Exported = true)]
public sealed class MainActivity : MauiAppCompatActivity
{
}
