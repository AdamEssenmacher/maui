namespace IosCompatActionSheetObserverRetentionRepro;

public sealed class ReproPage : ContentPage
{
	readonly Label _summary;
	bool _started;

	public ReproPage()
	{
		Title = "Compatibility ActionSheet observer retention";

		var runButton = new Button
		{
			Text = "Run repro"
		};
		runButton.Clicked += async (_, _) => await RunAsync();

		_summary = new Label
		{
			FontFamily = "Menlo",
			FontSize = 13,
			LineBreakMode = LineBreakMode.WordWrap,
			Text = "Ready."
		};

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = 24,
				Spacing = 16,
				Children =
				{
					runButton,
					_summary
				}
			}
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		if (!_started)
			await RunAsync();
	}

	async Task RunAsync()
	{
		if (_started)
			return;

		_started = true;
		_summary.Text = "Running...";

		var path = AutoRunSettings.GetResultsPath();
		string text;
		var exitCode = 0;

		try
		{
			await Task.Yield();

			var report = await ReproSession.RunAsync();
			text = report.ToText();
			exitCode = report.LeakProved ? 0 : 2;
		}
		catch (Exception ex)
		{
			text = "RESULT: ERROR" + Environment.NewLine + ex;
			exitCode = 3;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);

		if (AutoRunSettings.Enabled)
		{
			Environment.Exit(exitCode);
			return;
		}

		_summary.Text = text + Environment.NewLine + Environment.NewLine + "Results: " + path;
	}
}
