using System.Diagnostics;
using Microsoft.Maui.Controls.Maps;
using ControlsMap = Microsoft.Maui.Controls.Maps.Map;

namespace MapElementsSyncPerfRepro;

public sealed class SyncStressPage : ContentPage
{
	readonly ReproSession _session;
	readonly Label _statusLabel;
	readonly Label _resultLabel;
	bool _started;
	bool _heartbeatRunning;

	public SyncStressPage()
	{
		_session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		Title = _session.Options.Name;
		BackgroundColor = Colors.White;

		_statusLabel = new Label
		{
			Text = "Waiting to start.",
			Margin = new Thickness(12),
			Padding = new Thickness(8),
			FontSize = 13,
			TextColor = Colors.White,
			BackgroundColor = Color.FromArgb("#B0000000")
		};

		_resultLabel = new Label
		{
			Text = string.Empty,
			Margin = new Thickness(12),
			Padding = new Thickness(8),
			FontSize = 12,
			TextColor = Colors.White,
			BackgroundColor = Color.FromArgb("#B0000000")
		};

		Content = CreateOverlayLayout(new Label
		{
			Text = "Preparing scenario.",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center,
			TextColor = Color.FromArgb("#172026")
		});
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		if (_started)
			return;

		_started = true;
		StartHeartbeat();
		_session.Start();
		Dispatcher.Dispatch(async () => await RunStressAsync());
	}

	protected override void OnDisappearing()
	{
		_heartbeatRunning = false;
		base.OnDisappearing();
	}

	void StartHeartbeat()
	{
		_heartbeatRunning = true;
		Dispatcher.StartTimer(TimeSpan.FromMilliseconds(250), () =>
		{
			if (!_heartbeatRunning)
				return false;

			_session.MarkHeartbeat();
			return true;
		});
	}

	async Task RunStressAsync()
	{
		try
		{
			switch (_session.Options.Scenario)
			{
				case ReproScenario.GenerationControl:
					await RunGenerationControlAsync();
					break;
				case ReproScenario.DetachedPopulate:
					await RunDetachedPopulateAsync();
					break;
				case ReproScenario.LiveBurstAdd:
					await RunLiveAddAsync(paced: false);
					break;
				case ReproScenario.LivePacedAdd:
					await RunLiveAddAsync(paced: true);
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}

			_session.Complete();

			var result = await _session.Completion;
			_statusLabel.Text = "Completed.";
			_resultLabel.Text = result.ToDisplayText();
		}
		catch (Exception exception)
		{
			_session.Fail(exception);
			var result = await _session.Completion;
			_statusLabel.Text = "Failed.";
			_resultLabel.Text = result.ToDisplayText();
		}
	}

	async Task RunGenerationControlAsync()
	{
		var elements = GenerateElements();
		_session.RecordMapElementCount(0);
		_statusLabel.Text = $"Generated {elements.Count} {Options.ElementKind} elements without touching Map.MapElements.";
		await ObserveAsync("Holding generated elements");
	}

	async Task RunDetachedPopulateAsync()
	{
		var map = CreateMap();
		var elements = GenerateElements();

		_statusLabel.Text = $"Populating detached map with {elements.Count} elements.";
		var addStopwatch = Stopwatch.StartNew();
		for (var index = 0; index < elements.Count; index++)
		{
			map.MapElements.Add(elements[index]);
			_session.MarkAddedElement(index + 1, map.MapElements.Count);
			SetProgressText("Detached add", index + 1, elements.Count);
		}

		addStopwatch.Stop();
		_session.RecordAddElapsed(addStopwatch.Elapsed);
		_session.RecordMapElementCount(map.MapElements.Count);

		AttachMap(map);
		await ObserveAsync("Observing one initial handler sync");
	}

	async Task RunLiveAddAsync(bool paced)
	{
		var map = CreateMap();
		AttachMap(map);

		if (Options.LiveMapSettleMilliseconds > 0)
		{
			_statusLabel.Text = $"Waiting {Options.LiveMapSettleMilliseconds} ms for the live map handler.";
			await Task.Delay(Options.LiveMapSettleMilliseconds);
		}

		var elements = GenerateElements();
		var addStopwatch = Stopwatch.StartNew();
		_statusLabel.Text = paced
			? $"Adding {elements.Count} elements with UI yields."
			: $"Adding {elements.Count} elements in a tight UI-thread loop.";

		for (var index = 0; index < elements.Count; index++)
		{
			map.MapElements.Add(elements[index]);
			_session.MarkAddedElement(index + 1, map.MapElements.Count);
			SetProgressText(paced ? "Live paced add" : "Live burst add", index + 1, elements.Count);

			if (paced)
			{
				if (Options.PacedAddDelayMilliseconds > 0)
					await Task.Delay(Options.PacedAddDelayMilliseconds);
				else
					await Task.Yield();
			}
		}

		addStopwatch.Stop();
		_session.RecordAddElapsed(addStopwatch.Elapsed);
		_session.RecordMapElementCount(map.MapElements.Count);

		await ObserveAsync("Observing live map after add loop");
	}

	List<MapElement> GenerateElements()
	{
		var options = Options;
		var elements = new List<MapElement>(options.ElementCount);
		var generationStopwatch = Stopwatch.StartNew();
		_statusLabel.Text = $"Generating {options.ElementCount} {options.ElementKind} elements.";

		for (var index = 0; index < options.ElementCount; index++)
		{
			elements.Add(ElementFactory.CreateElement(options.ElementKind, index, options.Seed));
			_session.MarkGeneratedElement(index + 1);
			SetProgressText("Generate", index + 1, options.ElementCount);
		}

		generationStopwatch.Stop();
		_session.RecordGenerationElapsed(generationStopwatch.Elapsed);
		_session.RecordManagedMemoryAfter();
		return elements;
	}

	async Task ObserveAsync(string caption)
	{
		var observationStopwatch = Stopwatch.StartNew();
		var observeFor = TimeSpan.FromSeconds(Options.PostAddObservationSeconds);

		while (observationStopwatch.Elapsed < observeFor)
		{
			_statusLabel.Text = $"{caption}: {observationStopwatch.Elapsed.TotalSeconds:0.0}/{observeFor.TotalSeconds:0.0}s.";
			_session.RecordObservationElapsed(observationStopwatch.Elapsed);
			_session.RecordManagedMemoryAfter();
			await Task.Delay(250);
		}

		observationStopwatch.Stop();
		_session.RecordObservationElapsed(observationStopwatch.Elapsed);
		_session.RecordManagedMemoryAfter();
	}

	void SetProgressText(string caption, int completed, int total)
	{
		if (completed == 1 ||
			completed == total ||
			completed % Options.ProgressLogInterval == 0)
		{
			_statusLabel.Text = $"{caption}: {completed}/{total}.";
		}
	}

	ControlsMap CreateMap()
	{
		var map = new ControlsMap
		{
			IsShowingUser = false,
			IsTrafficEnabled = false
		};

		map.MoveToRegion(ElementFactory.CreateMapSpan());
		return map;
	}

	void AttachMap(ControlsMap map)
	{
		Content = CreateOverlayLayout(map);
	}

	Grid CreateOverlayLayout(View body)
	{
		var overlay = new Grid
		{
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Star),
				new RowDefinition(GridLength.Auto)
			}
		};

		overlay.Add(_statusLabel, 0, 0);
		overlay.Add(_resultLabel, 0, 2);

		return new Grid
		{
			Children =
			{
				body,
				overlay
			}
		};
	}

	ReproOptions Options => _session.Options;
}
