#nullable enable

using System;
using System.IO;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Platform;

namespace AndroidStackNavigationOnResumeRequestRetentionRepro;

[Activity(
	Theme = "@android:style/Theme.Material.Light.NoActionBar",
	MainLauncher = true,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : Activity
{
	protected override async void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);

		var output = new TextView(this)
		{
			Text = "Running Android StackNavigation delayed request retention repro..."
		};
		SetContentView(output);

		string text;
		try
		{
			var mauiContext = new MauiContext(new EmptyServiceProvider(), this);
			var report = await ReproSession.RunAsync(mauiContext);
			text = report.ToText();
		}
		catch (Exception ex)
		{
			text = "RESULT: FAILED" + System.Environment.NewLine + ex;
		}

		output.Text = text;

		var path = Path.Combine(FilesDir!.AbsolutePath, "autorun-results.txt");
		File.WriteAllText(path, text);
		Android.Util.Log.Info("AndroidStackNavigationOnResumeRequestRetentionLeakRepro", text);

		Process.KillProcess(Process.MyPid());
	}

	sealed class EmptyServiceProvider : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}
}
