using System.Text;

namespace BorderDashArrayLeakRepro;

internal static class DeviceProofRunner
{
	const string ProofEnvironmentVariable = "BORDER_DASH_PROOF";
	const string OutputEnvironmentVariable = "BORDER_DASH_PROOF_OUTPUT";
	static bool s_started;

	public static bool IsRequested =>
		IsEnabledSetting(ProofEnvironmentVariable) ||
		Environment.GetCommandLineArgs().Any(static arg => string.Equals(arg, "--device-proof", StringComparison.OrdinalIgnoreCase));

	public static async Task RunIfRequestedAsync(Label statusLabel, Label summaryLabel)
	{
		if (!IsRequested || s_started)
			return;

		s_started = true;
		await Task.Delay(500);

		var report = new StringBuilder();
		var started = DateTimeOffset.Now;
		report.AppendLine("BorderDashArrayLeakRepro device proof");
		report.AppendLine($"Started: {started:O}");
		report.AppendLine($"Platform: {DeviceInfo.Platform} {DeviceInfo.VersionString}");
		report.AppendLine($"Device: {DeviceInfo.Manufacturer} {DeviceInfo.Model} ({DeviceInfo.Idiom})");
		report.AppendLine();

		var scenarios = new[]
		{
			new ReproOptions(ReproMode.SolidBorderControl, 20, 64, 96, 3, 75),
			new ReproOptions(ReproMode.SharedAppResourceDashArray, 20, 64, 96, 3, 75),
			new ReproOptions(ReproMode.PerBorderDashArrayMitigation, 20, 64, 96, 3, 75)
		};

		try
		{
			foreach (var options in scenarios)
			{
				statusLabel.Text = $"Device proof running: {options.Name}";
				summaryLabel.Text = $"Running {options.Pages} pages with {options.CardsPerPage} cards/page. Results will be written to the app data directory.";

				var stats = await RunScenarioAsync(options, statusLabel);
				report.AppendLine(stats.ToSummary());
				report.AppendLine();
				summaryLabel.Text = stats.ToSummary();

				await Task.Delay(250);
			}

			var outputPath = WriteReport(report.ToString());
			statusLabel.Text = "Device proof completed.";
			summaryLabel.Text = report + Environment.NewLine + $"Report: {outputPath}";
		}
		catch (Exception ex)
		{
			report.AppendLine("FAILED");
			report.AppendLine(ex.ToString());
			var outputPath = WriteReport(report.ToString());
			statusLabel.Text = "Device proof failed.";
			summaryLabel.Text = ex + Environment.NewLine + $"Report: {outputPath}";
		}
		finally
		{
			await Task.Delay(1000);

			if (!IsDisabledSetting("BORDER_DASH_PROOF_QUIT"))
				Application.Current?.Quit();
		}
	}

	static async Task<ReproStats> RunScenarioAsync(ReproOptions options, Label statusLabel)
	{
		var session = new ReproSession(options);
		ReproSession.Current = session;

		var baseline = await MemorySampler.TakeAfterCollectionAsync();

		for (var i = 0; i < options.Pages; i++)
		{
			var cycle = session.BeginNextCycle();
			statusLabel.Text = $"Pushing page {cycle + 1}/{options.Pages}: {options.Name}";

			await Shell.Current.GoToAsync(AppShell.BorderLeakRoute, animate: false);

			if (options.DwellMilliseconds > 0)
				await Task.Delay(options.DwellMilliseconds);

			statusLabel.Text = $"Popping page {cycle + 1}/{options.Pages}: {options.Name}";
			await Shell.Current.GoToAsync("..", animate: false);
			await Task.Delay(150);
		}

		await Task.Delay(1000);
		var finalSnapshot = await MemorySampler.TakeAfterCollectionAsync();
		return session.GetStats(baseline, finalSnapshot);
	}

	static string WriteReport(string report)
	{
		var appDataPath = Path.Combine(FileSystem.AppDataDirectory, "border-dash-array-device-proof.txt");
		File.WriteAllText(appDataPath, report);

		var requestedPath = GetSetting(OutputEnvironmentVariable);
		if (!string.IsNullOrWhiteSpace(requestedPath))
			TryWriteRequestedReport(requestedPath, report);

		Console.WriteLine(report);
		Console.WriteLine($"Report: {appDataPath}");
		System.Diagnostics.Debug.WriteLine(report);
		System.Diagnostics.Debug.WriteLine($"Report: {appDataPath}");

		return appDataPath;
	}

	static void TryWriteRequestedReport(string requestedPath, string report)
	{
		try
		{
			var directory = Path.GetDirectoryName(requestedPath);
			if (!string.IsNullOrWhiteSpace(directory))
				Directory.CreateDirectory(directory);

			File.WriteAllText(requestedPath, report);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Unable to write requested report path '{requestedPath}': {ex}");
			System.Diagnostics.Debug.WriteLine($"Unable to write requested report path '{requestedPath}': {ex}");
		}
	}

	static bool IsEnabledSetting(string name)
	{
		var value = GetSetting(name);
		return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
	}

	static bool IsDisabledSetting(string name)
	{
		var value = GetSetting(name);
		return string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
	}

	static string? GetSetting(string name)
	{
		var value = Environment.GetEnvironmentVariable(name);
		if (!string.IsNullOrWhiteSpace(value))
			return value;

#if ANDROID
		var extras = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Intent?.Extras;
		if (extras?.ContainsKey(name) == true)
			return extras.Get(name)?.ToString();
#endif

		return null;
	}
}
