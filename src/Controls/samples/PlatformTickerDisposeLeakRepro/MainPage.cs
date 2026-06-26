#nullable enable

using System.Text;
using Microsoft.Maui.Animations;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace PlatformTickerDisposeLeakRepro;

public sealed class MainPage : ContentPage
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	readonly Label _status;

	public MainPage()
	{
		_status = new Label
		{
			Text = "Running PlatformTicker disposal leak repro...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		ReproResult result;

		try
		{
			result = await RunScenariosAsync();
		}
		catch (Exception ex)
		{
			var failure = "RESULT: ERROR" + Environment.NewLine + ex;
			_status.Text = failure;
			await WriteResultsAsync(failure);
			await Task.Delay(250);
			Environment.Exit(3);
			return;
		}

		var text = result.ToString();
		_status.Text = text;
		await WriteResultsAsync(text);
		await Task.Delay(250);
		Environment.Exit(result.IsProven ? 0 : 2);
	}

	static async Task<ReproResult> RunScenariosAsync()
	{
		var control = await RunDirectTickerScenarioAsync(stopBeforeRelease: true);
		var running = await RunDirectTickerScenarioAsync(stopBeforeRelease: false);
		var managerDisposed = await RunAnimationManagerScenarioAsync();

		return new ReproResult(control, running, managerDisposed);
	}

	static async Task<ScenarioResult> RunDirectTickerScenarioAsync(bool stopBeforeRelease)
	{
		var payloadRefs = new List<WeakReference>();
		var tickerRefs = new List<WeakReference>();

		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			for (var i = 0; i < Iterations; i++)
			{
				CreateDirectTicker(payloadRefs, tickerRefs, stopBeforeRelease);
			}
		});

		await WaitAndCollectAsync();

		return new ScenarioResult(
			stopBeforeRelease ? "direct-stop-control" : "direct-running-no-dispose",
			CountAlive(payloadRefs),
			CountAlive(tickerRefs),
			Iterations);
	}

	static void CreateDirectTicker(List<WeakReference> payloadRefs, List<WeakReference> tickerRefs, bool stopBeforeRelease)
	{
		var payload = new Payload(PayloadBytes);
		var ticker = new PlatformTicker();

		ticker.Fire = payload.Touch;
		ticker.Start();

		if (stopBeforeRelease)
			ticker.Stop();

		payloadRefs.Add(new WeakReference(payload));
		tickerRefs.Add(new WeakReference(ticker));
	}

	static async Task<ScenarioResult> RunAnimationManagerScenarioAsync()
	{
		var payloadRefs = new List<WeakReference>();
		var tickerRefs = new List<WeakReference>();
		var managerRefs = new List<WeakReference>();

		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			for (var i = 0; i < Iterations; i++)
			{
				CreateDisposedAnimationManager(payloadRefs, tickerRefs, managerRefs);
			}
		});

		await WaitAndCollectAsync();

		return new ScenarioResult(
			"animation-manager-running-dispose",
			CountAlive(payloadRefs),
			CountAlive(tickerRefs),
			Iterations,
			CountAlive(managerRefs));
	}

	static void CreateDisposedAnimationManager(List<WeakReference> payloadRefs, List<WeakReference> tickerRefs, List<WeakReference> managerRefs)
	{
		var payload = new Payload(PayloadBytes);
		var ticker = new PlatformTicker();
		var manager = new AnimationManager(ticker);
		var animation = new Microsoft.Maui.Animations.Animation(_ => payload.Touch(), duration: 3600);

		manager.Add(animation);
		manager.Dispose();

		payloadRefs.Add(new WeakReference(payload));
		tickerRefs.Add(new WeakReference(ticker));
		managerRefs.Add(new WeakReference(manager));
	}

	static async Task WaitAndCollectAsync()
	{
		await Task.Delay(1000);
		await Task.Run(ForceGc);
		await Task.Delay(250);
		await Task.Run(ForceGc);
	}

	static void ForceGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(250);
		}
	}

	static int CountAlive(List<WeakReference> refs)
	{
		var count = 0;

		foreach (var reference in refs)
		{
			if (reference.IsAlive)
				count++;
		}

		return count;
	}

	static async Task WriteResultsAsync(string text)
	{
		var paths = new[]
		{
			Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt"),
			Path.Combine(Path.GetTempPath(), "platformtickerdisposeleakrepro-results.txt")
		};

		foreach (var path in paths)
		{
			try
			{
				var directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrWhiteSpace(directory))
					Directory.CreateDirectory(directory);

				await File.WriteAllTextAsync(path, text);
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex);
			}
		}

		Console.WriteLine(text);
	}

	sealed class Payload
	{
		readonly byte[] _data;
		int _ticks;

		public Payload(int bytes)
		{
			_data = new byte[bytes];
			_data[0] = 123;
		}

		public void Touch()
		{
			_ticks++;
			if (_ticks == int.MaxValue)
				_ticks = _data[0];
		}
	}

	readonly record struct ScenarioResult(string Name, int PayloadsAlive, int TickersAlive, int Total, int ManagersAlive = -1)
	{
		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.Append(Name);
			builder.Append(": payloads=");
			builder.Append(PayloadsAlive);
			builder.Append('/');
			builder.Append(Total);
			builder.Append(", tickers=");
			builder.Append(TickersAlive);
			builder.Append('/');
			builder.Append(Total);

			if (ManagersAlive >= 0)
			{
				builder.Append(", managers=");
				builder.Append(ManagersAlive);
				builder.Append('/');
				builder.Append(Total);
			}

			return builder.ToString();
		}
	}

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult RunningTicker, ScenarioResult ManagerDisposed)
	{
		static int TotalLeakThreshold => Iterations / 2;

		public bool IsProven =>
			Control.PayloadsAlive == 0 &&
			Control.TickersAlive == 0 &&
			RunningTicker.PayloadsAlive >= TotalLeakThreshold &&
			ManagerDisposed.PayloadsAlive >= TotalLeakThreshold &&
			ManagerDisposed.ManagersAlive >= TotalLeakThreshold;

		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.AppendLine(IsProven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine(Control.ToString());
			builder.AppendLine(RunningTicker.ToString());
			builder.AppendLine(ManagerDisposed.ToString());
			builder.Append("payload-bytes-per-scenario=");
			builder.Append(Iterations * PayloadBytes);
			builder.AppendLine();
			builder.Append("app-data-directory=");
			builder.Append(FileSystem.AppDataDirectory);
			builder.AppendLine();
			builder.Append("dotnet-version=");
			builder.Append(Environment.Version);
			return builder.ToString();
		}
	}
}
