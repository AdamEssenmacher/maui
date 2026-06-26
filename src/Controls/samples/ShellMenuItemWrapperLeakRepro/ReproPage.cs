namespace ShellMenuItemWrapperLeakRepro;

public sealed class ReproPage : ContentPage
{
	readonly Label _summary;
	bool _started;

	public ReproPage()
	{
		Title = "Shell MenuItem wrapper leak";

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

	async Task RunAsync()
	{
		if (_started)
			return;

		_started = true;
		_summary.Text = "Running...";

		await Task.Yield();

		var report = ReproSession.Run();
		var path = AutoRunSettings.GetResultsPath();

		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, report.ToText());

		_summary.Text = report.ToText() + Environment.NewLine + Environment.NewLine + "Results: " + path;
	}
}
