using System.Diagnostics;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Platform;

namespace MauiSoftInputObserverLeakRepro;

public sealed class LeakProbePage : ContentPage
{
	const int CycleCount = 12;

	readonly Grid _host;
	readonly Label _status;
	readonly VerticalStackLayout _log;
	bool _started;

	public LeakProbePage()
	{
		Title = "SoftInput observer leak repro";
		BackgroundColor = Colors.White;

		_status = new Label
		{
			Text = "Waiting to start...",
			TextColor = Colors.Black,
			FontSize = 16,
			LineBreakMode = LineBreakMode.WordWrap
		};

		_log = new VerticalStackLayout
		{
			Spacing = 4
		};

		_host = new Grid
		{
			BackgroundColor = Color.FromArgb("#f3f6fb"),
			HeightRequest = 160
		};

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
						Text = "MAUI SoftInput Observer Leak Repro",
						TextColor = Colors.Black,
						FontSize = 22,
						FontAttributes = FontAttributes.Bold
					},
					_status,
					_host,
					_log
				}
			}
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		if (_started)
			return;

		_started = true;
		await Task.Delay(500);

		try
		{
			await RunAsync();
		}
		catch (Exception ex)
		{
			Log("ERROR: " + ex);
			_status.Text = "Repro failed: " + ex.Message;
			await ExitAsync(3);
		}
	}

	async Task RunAsync()
	{
		Log("Running control scenario: SafeAreaEdges.None");
		var none = await RunScenarioAsync("none", SafeAreaEdges.None);

		Log("Running suspect scenario: SafeAreaEdges.SoftInput");
		var softInput = await RunScenarioAsync("softinput", new SafeAreaEdges(SafeAreaRegions.SoftInput));

		var proof = softInput.PlatformAlive > 0 && none.PlatformAlive == 0;
		var summary =
			$"RESULT: {(proof ? "LEAK REPRODUCED" : "NOT PROVEN")}\n" +
			$"Control SafeAreaEdges.None: virtual={none.VirtualAlive}/{CycleCount}, handler={none.HandlerAlive}/{CycleCount}, platform={none.PlatformAlive}/{CycleCount}\n" +
			$"Suspect SafeAreaEdges.SoftInput: virtual={softInput.VirtualAlive}/{CycleCount}, handler={softInput.HandlerAlive}/{CycleCount}, platform={softInput.PlatformAlive}/{CycleCount}\n" +
			$"Result file: {WriteResultFile(none, softInput, proof)}";

		_status.Text = summary;
		Log(summary);

		await ExitAsync(proof ? 0 : 2);
	}

	async Task<ScenarioResult> RunScenarioAsync(string name, SafeAreaEdges safeAreaEdges)
	{
		var probes = new List<ProbeRefs>();

		for (var i = 0; i < CycleCount; i++)
		{
			probes.Add(await CreateAndRemoveProbeAsync(name, safeAreaEdges, i));
			await ForceGcAsync();
			Log($"{name} cycle {i + 1}: platform alive={probes.Count(p => p.PlatformView.IsAlive)}");
		}

		await Task.Delay(500);
		await ForceGcAsync();
		await ForceGcAsync();

		return new ScenarioResult(
			name,
			probes.Count(p => p.VirtualView.IsAlive),
			probes.Count(p => p.Handler.IsAlive),
			probes.Count(p => p.PlatformView.IsAlive));
	}

	async Task<ProbeRefs> CreateAndRemoveProbeAsync(string scenarioName, SafeAreaEdges safeAreaEdges, int index)
	{
		WeakReference? virtualView = null;
		WeakReference? handler = null;
		WeakReference? platformView = null;

		await MainThread.InvokeOnMainThreadAsync(async () =>
		{
			var probe = new Grid
			{
				SafeAreaEdges = safeAreaEdges,
				AutomationId = $"{scenarioName}-probe-{index}",
				HeightRequest = 96,
				BackgroundColor = scenarioName == "softinput" ? Color.FromArgb("#ffe8e8") : Color.FromArgb("#e8f1ff"),
				RowDefinitions =
				{
					new RowDefinition(GridLength.Auto),
					new RowDefinition(GridLength.Star)
				}
			};

			probe.Add(new Label
			{
				Text = $"{scenarioName} #{index}",
				TextColor = Colors.Black,
				Margin = new Thickness(8, 6, 8, 0)
			});

			probe.Add(new Entry
			{
				Text = "entry",
				Margin = new Thickness(8),
				AutomationId = $"{scenarioName}-entry-{index}"
			}, row: 1);

			_host.Children.Add(probe);
			await WaitUntilLoadedAsync(probe);
			await Task.Delay(100);

			var currentHandler = probe.Handler;
			var currentPlatformView = currentHandler?.PlatformView;

			if (currentHandler is null || currentPlatformView is null)
				throw new InvalidOperationException($"Probe {scenarioName} #{index} did not create a handler/platform view.");

			virtualView = new WeakReference(probe);
			handler = new WeakReference(currentHandler);
			platformView = new WeakReference(currentPlatformView);

			_host.Children.Remove(probe);
			probe.DisconnectHandlers();

			probe = null!;
			currentHandler = null;
			currentPlatformView = null;
		});

		await Task.Delay(250);
		await ForceGcAsync();

		return new ProbeRefs(
			virtualView ?? throw new InvalidOperationException("Missing virtual view reference."),
			handler ?? throw new InvalidOperationException("Missing handler reference."),
			platformView ?? throw new InvalidOperationException("Missing platform view reference."));
	}

	static async Task WaitUntilLoadedAsync(VisualElement element)
	{
		if (element.IsLoaded)
			return;

		var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		void OnLoaded(object? sender, EventArgs args)
		{
			element.Loaded -= OnLoaded;
			loaded.TrySetResult();
		}

		element.Loaded += OnLoaded;

		var completed = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(3)));
		element.Loaded -= OnLoaded;

		if (completed != loaded.Task)
			throw new TimeoutException("Probe view did not load.");
	}

	static async Task ForceGcAsync()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect(2, GCCollectionMode.Forced, blocking: true);
			await Task.Delay(50);
		}
	}

	string WriteResultFile(ScenarioResult none, ScenarioResult softInput, bool proof)
	{
		var path = Path.Combine(FileSystem.AppDataDirectory, "softinput-observer-leak-result.txt");
		var text =
			$"MauiView SoftInput observer leak repro\n" +
			$"Timestamp: {DateTimeOffset.Now:O}\n" +
			$"Proof: {proof}\n" +
			$"{none}\n" +
			$"{softInput}\n";

		File.WriteAllText(path, text);
		return path;
	}

	void Log(string message)
	{
		Debug.WriteLine(message);
		Console.WriteLine(message);

		_log.Children.Add(new Label
		{
			Text = message,
			TextColor = Colors.Black,
			FontSize = 12,
			LineBreakMode = LineBreakMode.WordWrap
		});
	}

	static async Task ExitAsync(int exitCode)
	{
		if (Environment.GetEnvironmentVariable("MAUI_REPRO_NO_EXIT") == "1")
			return;

		await Task.Delay(1000);
		Environment.Exit(exitCode);
	}

	readonly record struct ProbeRefs(WeakReference VirtualView, WeakReference Handler, WeakReference PlatformView);
	readonly record struct ScenarioResult(string Name, int VirtualAlive, int HandlerAlive, int PlatformAlive);
}
