#nullable enable

using System;
using System.IO;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using AndroidX.AppCompat.App;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;

namespace AndroidWrapperViewNativeStateRetentionRepro;

[Activity(
	Theme = "@style/Theme.MaterialComponents.DayNight.NoActionBar",
	MainLauncher = true,
	Exported = true,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : AppCompatActivity
{
	protected override async void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);

		var output = new TextView(this)
		{
			Text = "Running Android WrapperView native-state retention repro..."
		};
		SetContentView(output);

		string text;
		try
		{
			var services = new ServiceCollection().BuildServiceProvider();
			var context = new MauiContext(services, this);
			var report = await ReproSession.RunAsync(context);
			text = report.ToText();
		}
		catch (Exception ex)
		{
			text = "RESULT: FAILED" + System.Environment.NewLine + ex;
		}

		output.Text = text;

		var path = Path.Combine(FilesDir!.AbsolutePath, "autorun-results.txt");
		File.WriteAllText(path, text);
		Android.Util.Log.Info("AndroidWrapperViewNativeStateRetentionRepro", text);

		Process.KillProcess(Process.MyPid());
	}
}
