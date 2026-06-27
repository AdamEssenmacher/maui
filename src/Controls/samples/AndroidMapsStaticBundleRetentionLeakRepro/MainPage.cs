using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace AndroidMapsStaticBundleRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	bool _started;

	public MainPage()
	{
		Content = new Label
		{
			Text = "Running Android Maps static Bundle retention leak repro...",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center
		};
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		if (_started)
			return;

		_started = true;
		_ = RunAsync();
	}

	async Task RunAsync()
	{
		var activity = Platform.CurrentActivity
			?? throw new InvalidOperationException("No current Android Activity was available.");

		var report = await ReproSession.RunAsync(activity);
		var text = report.ToText();

		if (Content is Label label)
			label.Text = text;

		if (activity.FilesDir?.AbsolutePath is string filesDir)
		{
			var path = Path.Combine(filesDir, "autorun-results.txt");
			await File.WriteAllTextAsync(path, text);
		}
	}
}
