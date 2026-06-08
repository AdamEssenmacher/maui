using System.Text;

namespace PathGeometryFiguresClearLeakRepro;

public sealed class MainPage : ContentPage
{
	const string TargetName = "PathGeometryFigures";

	readonly string[] _args;
	readonly Label _statusLabel;
	readonly Editor _reportEditor;

	bool _autoRunStarted;
	bool _runInProgress;

	public MainPage(string[] args)
	{
		_args = args;
		Title = "PathGeometry Figures Clear";

		_statusLabel = new Label
		{
			Text = "Ready",
			LineBreakMode = LineBreakMode.WordWrap
		};

		_reportEditor = new Editor
		{
			AutoSize = EditorAutoSizeOption.Disabled,
			FontFamily = "Courier New",
			HeightRequest = 420,
			IsReadOnly = true,
			Text = "Run the repro to write autorun-results.txt."
		};

		var runAllButton = new Button { Text = "Run All" };
		runAllButton.Clicked += async (_, _) => await RunScenariosAsync(
			LeakScenarioKind.Control,
			LeakScenarioKind.LeakySharedFigureClear,
			LeakScenarioKind.MitigationSharedFigureRemoveAt);

		var controlButton = new Button { Text = "Control" };
		controlButton.Clicked += async (_, _) => await RunScenariosAsync(LeakScenarioKind.Control);

		var leakyButton = new Button { Text = "Leaky" };
		leakyButton.Clicked += async (_, _) => await RunScenariosAsync(LeakScenarioKind.LeakySharedFigureClear);

		var mitigationButton = new Button { Text = "Mitigation" };
		mitigationButton.Clicked += async (_, _) => await RunScenariosAsync(LeakScenarioKind.MitigationSharedFigureRemoveAt);

		var buttonGrid = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star)
			},
			ColumnSpacing = 8
		};

		buttonGrid.Add(runAllButton, 0, 0);
		buttonGrid.Add(controlButton, 1, 0);
		buttonGrid.Add(leakyButton, 2, 0);
		buttonGrid.Add(mitigationButton, 3, 0);

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(20),
				Spacing = 12,
				Children =
				{
					new Label
					{
						Text = "PathGeometry.Figures.Clear() leak repro",
						FontAttributes = FontAttributes.Bold,
						FontSize = 20,
						LineBreakMode = LineBreakMode.WordWrap
					},
					_statusLabel,
					buttonGrid,
					_reportEditor
				}
			}
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		if (_autoRunStarted || !HasArg("--auto-run"))
			return;

		_autoRunStarted = true;

		if (!string.Equals(GetArgValue("--target"), TargetName, StringComparison.OrdinalIgnoreCase))
		{
			await WriteFailureReportAsync($"Unsupported autorun target. Expected --target={TargetName}.");
			return;
		}

		await RunScenariosAsync(
			LeakScenarioKind.Control,
			LeakScenarioKind.LeakySharedFigureClear,
			LeakScenarioKind.MitigationSharedFigureRemoveAt);
	}

	async Task RunScenariosAsync(params LeakScenarioKind[] scenarios)
	{
		if (_runInProgress)
			return;

		_runInProgress = true;

		try
		{
			var options = CreateOptionsFromArgs();
			_statusLabel.Text = "Running...";
			_reportEditor.Text = string.Empty;

			var results = await LeakScenarioRunner.RunAsync(
				Navigation,
				options,
				scenarios,
				message =>
				{
					Dispatcher.Dispatch(() =>
					{
						_statusLabel.Text = message;
						_reportEditor.Text += message + Environment.NewLine;
					});
				},
				CancellationToken.None);

			var report = FormatReport(options, results);
			await WriteReportAsync(report);
			_reportEditor.Text = report;
			_statusLabel.Text = $"Finished. Report: {GetReportPath()}";
		}
		catch (Exception ex)
		{
			await WriteFailureReportAsync(ex.ToString());
		}
		finally
		{
			_runInProgress = false;
		}
	}

	LeakRunOptions CreateOptionsFromArgs()
	{
		return new LeakRunOptions(
			PageCount: GetPositiveIntArg("--pages", 50),
			PathsPerPage: GetPositiveIntArg("--paths-per-page", 24),
			PayloadMegabytesPerPage: GetPositiveIntArg("--payload-mb", 4),
			DwellMilliseconds: GetPositiveIntArg("--dwell-ms", 20));
	}

	bool HasArg(string name) =>
		_args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

	string? GetArgValue(string name)
	{
		var prefix = name + "=";

		foreach (var arg in _args)
		{
			if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				return arg[prefix.Length..];
		}

		return null;
	}

	int GetPositiveIntArg(string name, int defaultValue)
	{
		var value = GetArgValue(name);

		if (int.TryParse(value, out var parsed) && parsed > 0)
			return parsed;

		return defaultValue;
	}

	static string FormatReport(LeakRunOptions options, IReadOnlyList<LeakScenarioResult> results)
	{
		var builder = new StringBuilder();

		builder.AppendLine("PathGeometry Figures Clear Leak Repro");
		builder.AppendLine($"Generated: {DateTimeOffset.Now:O}");
		builder.AppendLine($"Options: pages={options.PageCount}, pathsPerPage={options.PathsPerPage}, payloadMBPerPage={options.PayloadMegabytesPerPage}, dwellMS={options.DwellMilliseconds}");
		builder.AppendLine();
		builder.AppendLine("Leak path under test:");
		builder.AppendLine("shared PathFigure -> PropertyChanged delegate -> page-local PathGeometry -> InvalidatePathGeometryRequested delegate -> Path -> BindingContext payload");
		builder.AppendLine();
		builder.AppendLine("Scenarios:");

		foreach (var result in results)
		{
			builder.AppendLine();
			builder.AppendLine($"{result.Name} ({result.Kind})");
			builder.AppendLine($"  Pages retained: {result.RetainedPages}/{result.TotalPages}");
			builder.AppendLine($"  Payload view models retained: {result.RetainedPayloads}/{result.TotalPayloads}");
			builder.AppendLine($"  Paths retained: {result.RetainedPaths}/{result.TotalPaths}");
			builder.AppendLine($"  PathGeometry instances retained: {result.RetainedGeometries}/{result.TotalGeometries}");
			builder.AppendLine($"  Retained payload bytes: {result.RetainedPayloadBytes} ({FormatBytes(result.RetainedPayloadBytes)})");
			builder.AppendLine($"  Managed memory before GC baseline: {result.ManagedBytesBefore} ({FormatBytes(result.ManagedBytesBefore)})");
			builder.AppendLine($"  Managed memory after forced GC: {result.ManagedBytesAfter} ({FormatBytes(result.ManagedBytesAfter)})");
			builder.AppendLine($"  Managed memory delta: {result.ManagedBytesDelta} ({FormatBytes(result.ManagedBytesDelta)})");
			builder.AppendLine($"  GC heap before baseline: {result.GcHeapBytesBefore} ({FormatBytes(result.GcHeapBytesBefore)})");
			builder.AppendLine($"  GC heap after forced GC: {result.GcHeapBytesAfter} ({FormatBytes(result.GcHeapBytesAfter)})");
			builder.AppendLine($"  GC heap delta: {result.GcHeapBytesDelta} ({FormatBytes(result.GcHeapBytesDelta)})");
			builder.AppendLine($"  Elapsed: {result.Elapsed}");
		}

		builder.AppendLine();
		builder.AppendLine("Expected result:");
		builder.AppendLine("control and mitigation should release nearly all tracked objects after forced full GC; leaky should retain most or all payloads, Paths, and PathGeometry instances.");

		return builder.ToString();
	}

	static string FormatBytes(long bytes)
	{
		const double OneKiB = 1024;
		const double OneMiB = OneKiB * 1024;

		if (Math.Abs(bytes) >= OneMiB)
			return $"{bytes / OneMiB:0.00} MiB";

		if (Math.Abs(bytes) >= OneKiB)
			return $"{bytes / OneKiB:0.00} KiB";

		return $"{bytes} B";
	}

	async Task WriteFailureReportAsync(string message)
	{
		var report = "PathGeometry Figures Clear Leak Repro failed." + Environment.NewLine + message;
		await WriteReportAsync(report);
		_reportEditor.Text = report;
		_statusLabel.Text = $"Failed. Report: {GetReportPath()}";
	}

	static async Task WriteReportAsync(string report)
	{
		var path = GetReportPath();
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
		await File.WriteAllTextAsync(path, report);
	}

	static string GetReportPath()
	{
		return System.IO.Path.Combine(
			FileSystem.AppDataDirectory,
			"PathGeometryFiguresClearLeakRepro",
			"autorun-results.txt");
	}
}
