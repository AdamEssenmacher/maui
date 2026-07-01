using System;
using System.IO;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace AndroidFlyoutViewNativeContentRetentionRepro;

public class MainPage : ContentPage
{
	readonly Label _status;
	bool _started;

	public MainPage()
	{
		Title = "Android FlyoutView Native Content Retention";
		_status = new Label
		{
			Text = "Waiting to run...",
			FontFamily = "monospace",
			FontSize = 13,
			LineBreakMode = LineBreakMode.WordWrap
		};

		Content = new VerticalStackLayout
		{
			Padding = 16,
			Spacing = 12,
			Children =
			{
				new Label
				{
					Text = "Android FlyoutView Native Content Retention",
					FontAttributes = FontAttributes.Bold,
					FontSize = 18
				},
				_status
			}
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		if (_started)
			return;

		_started = true;
		_status.Text = "Running repro...";

		string report;
		try
		{
			report = await ReproSession.RunAsync(this);
		}
		catch (Exception ex)
		{
			report = ex.ToString();
		}

		var path = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
		Directory.CreateDirectory(FileSystem.AppDataDirectory);
		await File.WriteAllTextAsync(path, report);
		_status.Text = report;
	}
}
