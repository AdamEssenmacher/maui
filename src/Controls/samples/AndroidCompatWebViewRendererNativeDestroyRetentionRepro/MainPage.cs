#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace AndroidCompatWebViewRendererNativeDestroyRetentionRepro;

public sealed class MainPage : ContentPage
{
	readonly Label _status;
	bool _ran;

	public MainPage()
	{
		Title = "Android WebViewRenderer Native Destroy Retention";
		_status = new Label
		{
			Text = "Running Android WebViewRenderer native destroy retention repro...",
			LineBreakMode = LineBreakMode.WordWrap
		};

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = 20,
				Spacing = 12,
				Children =
				{
					new Label
					{
						Text = "Android compatibility WebViewRenderer native destroy retention repro",
						FontAttributes = FontAttributes.Bold
					},
					_status
				}
			}
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		if (_ran)
			return;

		_ran = true;

		try
		{
			var context = await WaitForMauiContextAsync();
			var report = await ReproSession.RunAsync(context);
			var text = report.ToText();
			var path = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
			File.WriteAllText(path, text);
			_status.Text = text;
		}
		catch (Exception ex)
		{
			var text = "FAILED" + Environment.NewLine + ex;
			var path = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
			File.WriteAllText(path, text);
			_status.Text = text;
		}
	}

	static async Task<IMauiContext> WaitForMauiContextAsync()
	{
		for (var i = 0; i < 50; i++)
		{
			var context = Application.Current?.MainPage?.Handler?.MauiContext
				?? Application.Current?.Windows.FirstOrDefault()?.Handler?.MauiContext;
			if (context != null)
				return context;

			await Task.Delay(100);
		}

		throw new InvalidOperationException("Unable to resolve a MauiContext for the repro run.");
	}
}
