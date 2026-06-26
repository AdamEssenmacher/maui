namespace ResourcesProviderApplicationRetentionLeakRepro;

public sealed class ReproPage : ContentPage
{
	bool _hasRun;

	readonly Label _resultsLabel = new()
	{
		FontFamily = "Menlo",
		FontSize = 12,
		LineBreakMode = LineBreakMode.WordWrap
	};

	public ReproPage()
	{
		Title = "ResourcesProvider Application Retention";

		var runButton = new Button
		{
			Text = "Run repro"
		};
		runButton.Clicked += (_, _) => RunRepro();

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = 24,
				Spacing = 16,
				Children =
				{
					runButton,
					_resultsLabel
				}
			}
		};

		Loaded += OnLoaded;
	}

	void OnLoaded(object? sender, EventArgs e)
	{
		if (!AutoRunSettings.Enabled || _hasRun)
			return;

		_hasRun = true;
		Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), RunRepro);
	}

	async void RunRepro()
	{
		var report = await ReproSession.RunAsync();
		var text = report.ToText();
		_resultsLabel.Text = text;

		var path = AutoRunSettings.GetResultsPath();
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);

		if (AutoRunSettings.Enabled)
			Environment.Exit(report.LeakProved ? 0 : 2);
	}
}
