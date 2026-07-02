using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace VisualDiagnosticsOverlayScrollViewsRetentionRepro;

public sealed class MainPage : ContentPage
{
	const int ScrollViewCount = 128;
	const int PayloadBytes = 1024 * 1024;
	const int SurvivorTolerance = 2;

	readonly string? _resultsPath;
	readonly Label _status;
	readonly ContentView _testHost;
	bool _started;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		Title = "VisualDiagnosticsOverlay ScrollViews Retention";

		_status = new Label
		{
			Text = "Running VisualDiagnosticsOverlay ScrollViews retention repro...",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center,
			HorizontalTextAlignment = TextAlignment.Center
		};

		_testHost = new ContentView
		{
			WidthRequest = 720,
			HeightRequest = 420,
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center
		};

		Grid.SetRow(_testHost, 1);

		Content = new Grid
		{
			Padding = 24,
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Star)
			},
			Children =
			{
				_status,
				_testHost
			}
		};

		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		if (_started)
			return;

		_started = true;

		try
		{
			var result = await RunReproAsync();
			var report = result.ToReport();

			_status.Text = result.Proven
				? "PROVEN: visual-specific adorner removal retained removed ScrollViews through the diagnostics overlay."
				: "NOT PROVEN: removed ScrollViews did not remain alive.";

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(result.Proven ? 0 : 2);
		}
		catch (Exception ex)
		{
			var report = "VisualDiagnosticsOverlay ScrollViews retention repro failed." + Environment.NewLine + ex;
			_status.Text = report;

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(3);
		}
	}

	async Task<ReproResult> RunReproAsync()
	{
		var overlay = GetOverlay();
		overlay.RemoveAdorners();
		overlay.RemoveScrollableElementHandler();

		var control = await RunScenarioAsync(
			"Control: clear overlay scroll handlers after visual-specific adorner removal",
			clearOverlayScrollHandlers: true);

		overlay.RemoveAdorners();
		overlay.RemoveScrollableElementHandler();

		var current = await RunScenarioAsync(
			"Current MAUI behavior",
			clearOverlayScrollHandlers: false);

		var controlCollected = control.ScrollViewSurvivors <= SurvivorTolerance
			&& control.PayloadSurvivors <= SurvivorTolerance
			&& control.PayloadBufferSurvivors <= SurvivorTolerance
			&& control.OverlayScrollViewCount == 0;

		var currentRetained = current.ScrollViewSurvivors >= ScrollViewCount - SurvivorTolerance
			&& current.PayloadSurvivors >= ScrollViewCount - SurvivorTolerance
			&& current.PayloadBufferSurvivors >= ScrollViewCount - SurvivorTolerance
			&& current.OverlayScrollViewCount >= ScrollViewCount - SurvivorTolerance;

		return new ReproResult(control, current, controlCollected && currentRetained);
	}

	async Task<ScenarioResult> RunScenarioAsync(string name, bool clearOverlayScrollHandlers)
	{
		var overlay = GetOverlay();
		var scrollViewRefs = new List<WeakReference<PayloadScrollView>>(ScrollViewCount);
		var payloadRefs = new List<WeakReference<Payload>>(ScrollViewCount);
		var payloadBufferRefs = new List<WeakReference<byte[]>>(ScrollViewCount);

		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var i = 0; i < ScrollViewCount; i++)
		{
			await CreateAndDropScrollViewAsync(
				i,
				overlay,
				clearOverlayScrollHandlers,
				scrollViewRefs,
				payloadRefs,
				payloadBufferRefs);

			if (i % 16 == 0)
			{
				_status.Text = $"{name}: {i + 1}/{ScrollViewCount}";
				await Task.Yield();
			}
		}

		_testHost.Content = null;
		await WaitForCollectionAsync();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		return new ScenarioResult(
			name,
			clearOverlayScrollHandlers,
			overlay.ScrollViews.Count,
			CountAlive(scrollViewRefs),
			CountAlive(payloadRefs),
			CountAlive(payloadBufferRefs),
			heapBefore,
			heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	async Task CreateAndDropScrollViewAsync(
		int index,
		IVisualDiagnosticsOverlay overlay,
		bool clearOverlayScrollHandlers,
		List<WeakReference<PayloadScrollView>> scrollViewRefs,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<byte[]>> payloadBufferRefs)
	{
		var payload = new Payload(index, PayloadBytes);
		var scrollView = new PayloadScrollView(payload)
		{
			HeightRequest = 360,
			WidthRequest = 640,
			Content = new VerticalStackLayout
			{
				Padding = 16,
				Children =
				{
					new Label
					{
						Text = $"Diagnostic target {index}",
						FontSize = 18
					},
					new Label
					{
						Text = new string('x', 4096)
					}
				}
			}
		};

		_testHost.Content = scrollView;
		await WaitForHandlerAsync(scrollView);

		if (!overlay.AddAdorner(scrollView, scrollToElement: false))
			throw new InvalidOperationException("Failed to add diagnostics adorner for the scroll view.");

		if (!overlay.RemoveAdorners(scrollView))
			throw new InvalidOperationException("Failed to remove diagnostics adorner for the scroll view.");

		if (clearOverlayScrollHandlers)
			overlay.RemoveScrollableElementHandler();

		_testHost.Content = null;
		scrollView.Content = null;
		scrollView.BindingContext = null;

		scrollViewRefs.Add(new WeakReference<PayloadScrollView>(scrollView));
		payloadRefs.Add(new WeakReference<Payload>(payload));
		payloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
	}

	IVisualDiagnosticsOverlay GetOverlay()
	{
		if (Window?.VisualDiagnosticsOverlay is { } overlay)
			return overlay;

		throw new InvalidOperationException("The page window does not expose a VisualDiagnosticsOverlay.");
	}

	static async Task WaitForHandlerAsync(VisualElement element)
	{
		for (var i = 0; i < 100; i++)
		{
			if (element.Handler != null)
			{
				await Task.Delay(25);
				return;
			}

			await Task.Delay(25);
		}

		throw new InvalidOperationException($"Handler was not created for {element.GetType().Name}.");
	}

	static async Task WaitForCollectionAsync()
	{
		for (var i = 0; i < 6; i++)
		{
			ForceFullGc();
			await Task.Delay(50);
		}
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	static int CountAlive<T>(IEnumerable<WeakReference<T>> references)
		where T : class
	{
		var count = 0;
		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out _))
				count++;
		}

		return count;
	}

	readonly record struct ScenarioResult(
		string Name,
		bool ClearOverlayScrollHandlers,
		int OverlayScrollViewCount,
		int ScrollViewSurvivors,
		int PayloadSurvivors,
		int PayloadBufferSurvivors,
		long HeapBeforeBytes,
		long HeapAfterBytes)
	{
		public long HeapDeltaBytes => HeapAfterBytes - HeapBeforeBytes;
		public double RetainedPayloadMiB => PayloadBufferSurvivors * PayloadBytes / 1024d / 1024d;

		public void AppendTo(StringBuilder builder)
		{
			builder.AppendLine(Name);
			builder.AppendLine($"  Clear overlay scroll handlers: {ClearOverlayScrollHandlers}");
			builder.AppendLine($"  Overlay ScrollViews count: {OverlayScrollViewCount}");
			builder.AppendLine($"  ScrollView survivors: {ScrollViewSurvivors}/{ScrollViewCount}");
			builder.AppendLine($"  Payload survivors: {PayloadSurvivors}/{ScrollViewCount}");
			builder.AppendLine($"  Payload buffer survivors: {PayloadBufferSurvivors}/{ScrollViewCount}");
			builder.AppendLine($"  Retained payload estimate: {RetainedPayloadMiB:F1} MiB");
			builder.AppendLine($"  Managed heap before: {HeapBeforeBytes:N0} bytes");
			builder.AppendLine($"  Managed heap after: {HeapAfterBytes:N0} bytes");
			builder.AppendLine($"  Managed heap delta: {HeapDeltaBytes:N0} bytes");
		}
	}

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult Current, bool Proven)
	{
		public string ToReport()
		{
			var builder = new StringBuilder();
			builder.AppendLine("VisualDiagnosticsOverlay ScrollViews retention repro");
			builder.AppendLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
			builder.AppendLine();
			Control.AppendTo(builder);
			builder.AppendLine();
			Current.AppendTo(builder);
			builder.AppendLine();
			builder.AppendLine("Expected proof signal:");
			builder.AppendLine($"- Control survivors <= {SurvivorTolerance} and overlay ScrollViews count is 0 after explicit scroll-handler cleanup.");
			builder.AppendLine($"- Current behavior survivors >= {ScrollViewCount - SurvivorTolerance} after RemoveAdorners(IVisualTreeElement) removes the last adorner without clearing scroll handlers.");
			builder.AppendLine();
			builder.AppendLine("Retained graph under current behavior:");
			builder.AppendLine("Live Window -> VisualDiagnosticsOverlay -> _scrollViews key -> removed PayloadScrollView -> 1 MiB payload");
			builder.AppendLine();
			builder.AppendLine("Platform listener state:");
			builder.AppendLine("On iOS/Mac Catalyst, the same _scrollViews entry also stores the KVO observer disposable created for the native UIScrollView contentOffset observer.");
			return builder.ToString();
		}
	}
}
