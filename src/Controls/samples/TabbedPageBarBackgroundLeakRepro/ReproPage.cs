using System.Text;
using Microsoft.Maui.Storage;

namespace Maui.Controls.Sample.TabbedPageBarBackgroundLeakRepro;

public class TabbedPageBarBackgroundLeakReproPage : ContentPage
{
	const string ResultFileName = "tabbedpage-barbackground-leak-result.txt";

	readonly Label _detailsLabel;
	readonly Label _resultLabel;
	readonly Button _runButton;
	bool _autoRunStarted;
	bool _isRunning;

	public TabbedPageBarBackgroundLeakReproPage()
	{
		Title = "TabbedPage BarBackground Leak";

		_runButton = new Button
		{
			Text = "Run leak and control probes",
			AutomationId = "RunLeakAndControlProbes"
		};
		_runButton.Clicked += async (_, _) => await RunProbeAsync();

		_resultLabel = new Label
		{
			AutomationId = "Result",
			FontAttributes = FontAttributes.Bold,
			FontSize = 18
		};

		_detailsLabel = new Label
		{
			AutomationId = "Details",
			FontFamily = "monospace",
			FontSize = 13
		};

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(24),
				Spacing = 14,
				Children =
				{
					new Label
					{
						Text = "TabbedPage app resource GradientBrush leak repro",
						FontSize = 23,
						FontAttributes = FontAttributes.Bold
					},
					new Label
					{
						Text = "The leak path removes a TabbedPage whose app resource Style sets BarBackground to a shared LinearGradientBrush. The control clears BarBackground before removal.",
						FontSize = 16
					},
					_runButton,
					_resultLabel,
					_detailsLabel
				}
			}
		};

		_resultLabel.Text = "Run the probes to replace the Window.Page twice.";
		Loaded += OnLoaded;
	}

	async Task RunProbeAsync()
	{
		if (_isRunning)
			return;

		_isRunning = true;
		_runButton.IsEnabled = false;
		_resultLabel.Text = "Running...";
		_detailsLabel.Text = string.Empty;

		try
		{
			var leakRun = await TabbedPageBarBackgroundLeakProbe.CreateRunAsync(
				this,
				"Leak path",
				TabbedPageBarBackgroundLeakProbe.LeakStyle,
				TabbedPageBarBackgroundLeakProbe.LeakBrush,
				clearBarBackgroundBeforeDisconnect: false);

			var leakSnapshot = await TabbedPageBarBackgroundLeakProbe.CollectAsync(leakRun);

			var controlRun = await TabbedPageBarBackgroundLeakProbe.CreateRunAsync(
				this,
				"Control path",
				TabbedPageBarBackgroundLeakProbe.ControlStyle,
				TabbedPageBarBackgroundLeakProbe.ControlBrush,
				clearBarBackgroundBeforeDisconnect: true);

			var controlSnapshot = await TabbedPageBarBackgroundLeakProbe.CollectAsync(controlRun);

			Report(leakSnapshot, controlSnapshot);
			await WriteReportAsync();
		}
		catch (Exception ex)
		{
			_resultLabel.Text = "Probe failed.";
			_detailsLabel.Text = ex.ToString();
			await WriteReportAsync();
		}
		finally
		{
			_runButton.IsEnabled = true;
			_isRunning = false;
		}
	}

	void Report(ProbeSnapshot leak, ProbeSnapshot control)
	{
		var leakProven =
			leak.BrushSubscriberCount > 0 &&
			leak.SubscriberTargetAlive &&
			control.BrushSubscriberCount == 0;

		_resultLabel.Text = leakProven
			? "Leak reproduced: app resource style gradient retained the removed TabbedPage renderer/manager."
			: "Leak not proven by current run.";

		var details = new StringBuilder();
		Append(details, leak);
		details.AppendLine();
		Append(details, control);

		_detailsLabel.Text = details.ToString();
	}

	static void Append(StringBuilder details, ProbeSnapshot snapshot)
	{
		details.AppendLine(snapshot.Name);
		details.AppendLine($"  Brush subscribers: {snapshot.BrushSubscriberCount}");
		details.AppendLine($"  Subscriber targets: {snapshot.BrushSubscriberTargets}");
		details.AppendLine($"  Brush parent alive: {(snapshot.BrushParentAlive ? "yes" : "no")}");
		details.AppendLine($"  Captured subscriber type: {snapshot.SubscriberTargetType}");
		details.AppendLine($"  Captured subscriber target: {(snapshot.SubscriberTargetAlive ? "alive" : "collected")}");
		details.AppendLine($"  Previous child page: {(snapshot.PreviousChildPageAlive ? "alive" : "collected")}");
		details.AppendLine($"  TabbedPage: {(snapshot.TabbedPageAlive ? "alive" : "collected")}");
		details.AppendLine($"  {snapshot.Verdict}");
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		if (_autoRunStarted)
			return;

		_autoRunStarted = true;
		await Task.Delay(500);
		await RunProbeAsync();
	}

	async Task WriteReportAsync()
	{
		var report = $"{_resultLabel.Text}{Environment.NewLine}{Environment.NewLine}{_detailsLabel.Text}";
		var path = Path.Combine(FileSystem.Current.AppDataDirectory, ResultFileName);

		await File.WriteAllTextAsync(path, report);
		Console.WriteLine($"TabbedPageBarBackgroundLeakRepro result written to {path}");
		Console.WriteLine(report);
	}
}
