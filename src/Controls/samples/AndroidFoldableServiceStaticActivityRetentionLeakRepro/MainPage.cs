using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace AndroidFoldableServiceStaticActivityRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	bool _started;

	public MainPage()
	{
		Content = new Label
		{
			Text = "Running Android FoldableService static Activity retention leak repro...",
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
		var report = await ReproSession.RunAsync();
		var text = report.ToText();

		if (Content is Label label)
			label.Text = text;

		var activity = Platform.CurrentActivity;
		if (activity?.FilesDir?.AbsolutePath is string filesDir)
		{
			var path = Path.Combine(filesDir, "autorun-results.txt");
			await File.WriteAllTextAsync(path, text);
		}
	}
}
