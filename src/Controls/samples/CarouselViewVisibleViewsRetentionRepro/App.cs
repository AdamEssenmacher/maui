using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;

namespace CarouselViewVisibleViewsRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new RunnerPage());
	}
}

sealed class RunnerPage : ContentPage
{
	bool _ran;

	public RunnerPage()
	{
		Content = new Label
		{
			Text = "Running CarouselView VisibleViews retention repro...",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await TryRunAsync();
	}

	protected override async void OnHandlerChanged()
	{
		base.OnHandlerChanged();
		await TryRunAsync();
	}

	async Task TryRunAsync()
	{
		if (_ran || Handler?.MauiContext is null)
			return;

		_ran = true;
		await Task.Delay(250);

		try
		{
			var report = ReproSession.Run();
			var text = report.ToText();
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(report.LeakProved ? 0 : 2);
		}
		catch (Exception ex)
		{
			var text = "CarouselViewVisibleViewsRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/carouselview-visibleviews-retention-results.txt";

	public const int CarouselCount = 96;
	public const int VisibleViewsPerCarousel = 3;
	const int PayloadBytes = 512 * 1024;

	public static ReproReport Run()
	{
		var control = RunScenario(clearVisibleViewsOnTearDown: true);
		var current = RunScenario(clearVisibleViewsOnTearDown: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearVisibleViewsOnTearDown)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedVisibleViewHandles = new List<ObservableCollection<View>>(CarouselCount);
		var carouselReferences = new List<WeakReference<CarouselView>>(CarouselCount);
		var visibleViewReferences = new List<WeakReference<View>>(CarouselCount * VisibleViewsPerCarousel);
		var payloadReferences = new List<WeakReference<VisibleTilePayload>>(CarouselCount * VisibleViewsPerCarousel);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(CarouselCount * VisibleViewsPerCarousel);
		var retainedCollectionItemCounts = new List<int>(CarouselCount);

		for (var carouselIndex = 0; carouselIndex < CarouselCount; carouselIndex++)
		{
			CreateAndDiscardCarousel(
				clearVisibleViewsOnTearDown,
				carouselIndex,
				retainedVisibleViewHandles,
				carouselReferences,
				visibleViewReferences,
				payloadReferences,
				payloadBufferReferences,
				retainedCollectionItemCounts);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			retainedVisibleViewHandles.Count,
			Sum(retainedCollectionItemCounts),
			CountAlive(carouselReferences),
			CountAlive(visibleViewReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedVisibleViewHandles);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateAndDiscardCarousel(
		bool clearVisibleViewsOnTearDown,
		int carouselIndex,
		List<ObservableCollection<View>> retainedVisibleViewHandles,
		List<WeakReference<CarouselView>> carouselReferences,
		List<WeakReference<View>> visibleViewReferences,
		List<WeakReference<VisibleTilePayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences,
		List<int> retainedCollectionItemCounts)
	{
		var carousel = new CarouselView
		{
			AutomationId = $"Catalog carousel {carouselIndex:000}",
			Loop = true,
			PeekAreaInsets = new Thickness(56, 0),
			BindingContext = new { Route = $"tenant://workspace/{carouselIndex:000}/carousel" }
		};

		var visibleViews = carousel.VisibleViews;

		for (var visibleIndex = 0; visibleIndex < VisibleViewsPerCarousel; visibleIndex++)
		{
			var payload = new VisibleTilePayload(
				$"tile-{carouselIndex:000}-{visibleIndex:000}",
				$"Visible product tile {visibleIndex + 1} for workspace {carouselIndex:000}; includes cached badges, price history, and personalization metadata.",
				new byte[PayloadBytes]);
			payload.Buffer[0] = (byte)visibleIndex;
			payload.Buffer[^1] = (byte)(255 - visibleIndex);

			var view = CreateRealizedItemView(payload);
			view.Parent = carousel;
			visibleViews.Add(view);

			visibleViewReferences.Add(new WeakReference<View>(view));
			payloadReferences.Add(new WeakReference<VisibleTilePayload>(payload));
			payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));
		}

		if (clearVisibleViewsOnTearDown)
			visibleViews.Clear();

		retainedCollectionItemCounts.Add(visibleViews.Count);
		carouselReferences.Add(new WeakReference<CarouselView>(carousel));
		retainedVisibleViewHandles.Add(visibleViews);

		carousel = null!;
		visibleViews = null!;
	}

	static View CreateRealizedItemView(VisibleTilePayload payload)
	{
		return new Grid
		{
			BindingContext = payload,
			Padding = new Thickness(12),
			Children =
			{
				new Label
				{
					Text = payload.DisplayTitle,
					BindingContext = payload
				}
			}
		};
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

	static int Sum(IEnumerable<int> values)
	{
		var result = 0;
		foreach (var value in values)
			result += value;

		return result;
	}

	static void ForceGc()
	{
		for (var i = 0; i < 6; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(75);
		}
	}
}

sealed class VisibleTilePayload
{
	public VisibleTilePayload(string id, string description, byte[] buffer)
	{
		Id = id;
		Description = description;
		Buffer = buffer;
	}

	public string Id { get; }
	public string Description { get; }
	public byte[] Buffer { get; }
	public string DisplayTitle => $"{Id}: {Description}";
}

readonly record struct ScenarioResult(
	int RetainedVisibleViewHandles,
	int RetainedCollectionItemCount,
	int CarouselsAlive,
	int VisibleViewsAlive,
	int PayloadsAlive,
	int PayloadBuffersAlive,
	long HeapBefore,
	long HeapAfter)
{
	public long HeapDelta => HeapAfter - HeapBefore;
	public long RetainedPayloadBytes => (long)PayloadBuffersAlive * 512 * 1024;
}

readonly record struct ReproReport(ScenarioResult Control, ScenarioResult Current)
{
	public int RealizedViewCount => ReproSession.CarouselCount * ReproSession.VisibleViewsPerCarousel;

	public bool LeakProved =>
		Control.PayloadBuffersAlive == 0 &&
		Control.VisibleViewsAlive == 0 &&
		Current.RetainedCollectionItemCount == RealizedViewCount &&
		Current.PayloadBuffersAlive == RealizedViewCount &&
		Current.VisibleViewsAlive == RealizedViewCount;

	public string ToText()
	{
		var builder = new StringBuilder();
		builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
		builder.AppendLine("CarouselViewVisibleViewsRetentionRepro");
		builder.AppendLine("Run signature: visibleviews-payload-retention-v2");
		builder.AppendLine($"CarouselView.VisibleViews handles retained in both scenarios: {Current.RetainedVisibleViewHandles}");
		builder.AppendLine($"Realized item views per discarded CarouselView: {ReproSession.VisibleViewsPerCarousel}");
		builder.AppendLine("Payload per realized visible item view: 0.5 MiB");
		builder.AppendLine();
		AppendScenario(builder, "control: clear public VisibleViews handle at teardown", Control, RealizedViewCount);
		builder.AppendLine();
		AppendScenario(builder, "current: retain public VisibleViews handle without framework cleanup", Current, RealizedViewCount);
		builder.AppendLine();
		builder.AppendLine("Leak path: app/helper retained ObservableCollection<View> from CarouselView.VisibleViews -> realized item view -> BindingContext/Payload buffer.");
		builder.AppendLine("MAUI handler evidence: iOS, Android, and Tizen CarouselView handlers add realized item views to VisibleViews and only remove them during later visible-set diffs; teardown paths do not clear the public collection.");
		builder.AppendLine($"dotnet-version: {Environment.Version}");
		return builder.ToString();
	}

	static void AppendScenario(StringBuilder builder, string title, ScenarioResult result, int realizedViewCount)
	{
		builder.AppendLine($"Run: {title}");
		builder.AppendLine($"  retained VisibleViews handles: {result.RetainedVisibleViewHandles}");
		builder.AppendLine($"  retained collection item count: {result.RetainedCollectionItemCount}");
		builder.AppendLine($"  discarded CarouselViews alive after full GC: {result.CarouselsAlive}/{ReproSession.CarouselCount}");
		builder.AppendLine($"  realized item views alive after full GC: {result.VisibleViewsAlive}/{realizedViewCount}");
		builder.AppendLine($"  visible item payloads alive after full GC: {result.PayloadsAlive}/{realizedViewCount}");
		builder.AppendLine($"  visible item payload buffers alive after full GC: {result.PayloadBuffersAlive}/{realizedViewCount}");
		builder.AppendLine($"  retained visible payload bytes: {FormatBytes(result.RetainedPayloadBytes)}");
		builder.AppendLine($"  managed heap delta: {FormatBytes(result.HeapDelta)}");
	}

	static string FormatBytes(long bytes)
	{
		var mib = bytes / 1024d / 1024d;
		return $"{mib:0.0} MiB";
	}
}
