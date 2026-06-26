namespace DualScreenInfoHandlerChangedLeakRepro;

public sealed class ReproPage : ContentPage
{
	readonly Label _resultsLabel = new()
	{
		FontFamily = "Menlo",
		FontSize = 12,
		LineBreakMode = LineBreakMode.WordWrap
	};

	public ReproPage()
	{
		Title = "DualScreenInfo HandlerChanged Leak";

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

	void RunRepro()
	{
		var report = ReproSession.Run();
		_resultsLabel.Text = report.ToText();
		var path = AutoRunSettings.GetResultsPath();
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, report.ToText());
	}
}
