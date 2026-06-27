#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace AndroidImageRendererMotionEventHelperRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	readonly Label _status;

	public MainPage()
	{
		Title = "ImageRenderer leak repro";
		_status = new Label
		{
			AutomationId = "StatusLabel",
			FontFamily = "monospace",
			LineBreakMode = LineBreakMode.WordWrap,
			Text = "Running..."
		};

		Content = new ScrollView
		{
			Content = _status,
			Padding = 16
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		try
		{
			for (var i = 0; i < 20 && Handler?.MauiContext is null; i++)
				await Task.Delay(50);

			if (Handler?.MauiContext is not { } mauiContext)
				throw new InvalidOperationException("MauiContext is not available.");

			var report = await ReproSession.RunAsync(mauiContext);
			var text = report.ToText();
			_status.Text = text;
			Android.Util.Log.Info("AndroidImageRendererMotionEventHelperRetentionLeakRepro", text);

			var path = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
			File.WriteAllText(path, text);
		}
		catch (Exception ex)
		{
			_status.Text = ex.ToString();
			Android.Util.Log.Error("AndroidImageRendererMotionEventHelperRetentionLeakRepro", ex.ToString());
		}
	}
}
