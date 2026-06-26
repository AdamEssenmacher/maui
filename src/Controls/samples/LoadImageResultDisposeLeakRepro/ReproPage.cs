namespace LoadImageResultDisposeLeakRepro;

public sealed class ReproPage : ContentPage
{
	readonly Label _resultsLabel = new()
	{
		FontFamily = "Menlo",
		FontSize = 12,
		LineBreakMode = LineBreakMode.WordWrap
	};
	bool _started;

	public ReproPage()
	{
		Title = "LoadImage Result Dispose Leak";

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
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		if (AutoRunSettings.Enabled && !_started)
			RunRepro();
	}

	void RunRepro()
	{
		if (_started)
			return;

		_started = true;
		var report = ReproSession.Run();
		_resultsLabel.Text = report.ToText();
		var path = AutoRunSettings.GetResultsPath();
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, report.ToText());

		if (AutoRunSettings.Enabled)
			Environment.Exit(report.LeakProved ? 0 : 2);
	}
}
