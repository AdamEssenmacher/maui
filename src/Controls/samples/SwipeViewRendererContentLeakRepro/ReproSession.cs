using Foundation;
using UIKit;
using LegacySwipeViewRenderer = Microsoft.Maui.Controls.Compatibility.Platform.iOS.SwipeViewRenderer;

namespace SwipeViewRendererContentLeakRepro;

internal static class ReproSession
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly List<View> CachedContents = new();

	public static ReproReport Run()
	{
		ForceFullGc();

		var replacementWithoutRendererControl = RunReplacementWithoutRendererControl();
		CachedContents.Clear();
		ForceFullGc();

		var replacedContent = RunDisposedRendererReplacementScenario();

		var leakProved =
			replacementWithoutRendererControl.PayloadsAlive == 0 &&
			replacementWithoutRendererControl.ScrollParentsAlive == 0 &&
			replacementWithoutRendererControl.CachedContentsWithParent == 0 &&
			replacedContent.CachedContentsWithParent == 0 &&
			replacedContent.PayloadsAlive >= Iterations * 9 / 10 &&
			replacedContent.RenderersAlive >= Iterations * 9 / 10;

		return new ReproReport(
			Iterations,
			PayloadBytes,
			replacementWithoutRendererControl,
			replacedContent,
			leakProved);
	}

	static ScenarioResult RunReplacementWithoutRendererControl()
	{
		var payloads = new List<WeakReference<Payload>>(Iterations);
		var scrollParents = new List<WeakReference<ScrollView>>(Iterations);

		for (var i = 0; i < Iterations; i++)
			CreateReplacementCycleWithoutRenderer(i, payloads, scrollParents);

		var cachedContentsWithParent = CountCachedContentsWithParent();

		ForceFullGc();

		return new ScenarioResult(
			"cached replaced Content without renderer control",
			CountAlive(payloads),
			CountAlive(scrollParents),
			0,
			cachedContentsWithParent);
	}

	static ScenarioResult RunDisposedRendererReplacementScenario()
	{
		var payloads = new List<WeakReference<Payload>>(Iterations);
		var scrollParents = new List<WeakReference<ScrollView>>(Iterations);
		var renderers = new List<WeakReference<LegacySwipeViewRenderer>>(Iterations);

		for (var i = 0; i < Iterations; i++)
			CreateDisposedRendererReplacementCycle(i, payloads, scrollParents, renderers);

		var cachedContentsWithParent = CountCachedContentsWithParent();

		ForceFullGc();

		return new ScenarioResult(
			"cached replaced Content with disposed SwipeViewRenderer",
			CountAlive(payloads),
			CountAlive(scrollParents),
			CountAlive(renderers),
			cachedContentsWithParent);
	}

	static void CreateReplacementCycleWithoutRenderer(
		int index,
		List<WeakReference<Payload>> payloads,
		List<WeakReference<ScrollView>> scrollParents)
	{
		var payload = new Payload(index, PayloadBytes);
		var scrollParent = new ScrollView
		{
			BindingContext = payload,
			HeightRequest = 64,
			WidthRequest = 320
		};
		var swipeView = new SwipeView
		{
			HeightRequest = 64,
			WidthRequest = 320
		};
		var cachedContent = CreateCachedContent(index);

		scrollParent.Content = swipeView;
		swipeView.Content = cachedContent;
		CachedContents.Add(cachedContent);
		swipeView.Content = CreateReplacementContent(index);

		payloads.Add(new WeakReference<Payload>(payload));
		scrollParents.Add(new WeakReference<ScrollView>(scrollParent));

		scrollParent.Content = null;

		payload = null!;
		scrollParent = null!;
		swipeView = null!;
		cachedContent = null!;
	}

	static void CreateDisposedRendererReplacementCycle(
		int index,
		List<WeakReference<Payload>> payloads,
		List<WeakReference<ScrollView>> scrollParents,
		List<WeakReference<LegacySwipeViewRenderer>> renderers)
	{
		using var autoreleasePool = new NSAutoreleasePool();
		using var window = new UIWindow();

		var payload = new Payload(index, PayloadBytes);
		var scrollParent = new ScrollView
		{
			BindingContext = payload,
			HeightRequest = 64,
			WidthRequest = 320
		};
		var swipeView = new SwipeView
		{
			HeightRequest = 64,
			WidthRequest = 320
		};
		var cachedContent = CreateCachedContent(index);
		var renderer = new LegacySwipeViewRenderer();

		scrollParent.Content = swipeView;
		swipeView.Content = cachedContent;
		CachedContents.Add(cachedContent);

		renderer.SetElement(swipeView);
		renderer.WillMoveToWindow(window);

		swipeView.Content = CreateReplacementContent(index);

		payloads.Add(new WeakReference<Payload>(payload));
		scrollParents.Add(new WeakReference<ScrollView>(scrollParent));
		renderers.Add(new WeakReference<LegacySwipeViewRenderer>(renderer));

		renderer.Dispose();
		scrollParent.Content = null;

		payload = null!;
		scrollParent = null!;
		swipeView = null!;
		cachedContent = null!;
		renderer = null!;
	}

	static int CountCachedContentsWithParent()
	{
		var count = 0;

		foreach (var content in CachedContents)
		{
			if (content.Parent != null)
				count++;
		}

		return count;
	}

	static View CreateCachedContent(int index)
	{
		return new Grid
		{
			BindingContext = $"cached-content-{index}",
			HeightRequest = 64,
			WidthRequest = 320,
			Children =
			{
				new Label
				{
					Text = $"Cached row {index}",
					BindingContext = $"cached-label-{index}"
				}
			}
		};
	}

	static View CreateReplacementContent(int index)
	{
		return new Grid
		{
			BindingContext = $"replacement-content-{index}",
			HeightRequest = 64,
			WidthRequest = 320,
			Children =
			{
				new Label
				{
					Text = $"Replacement row {index}",
					BindingContext = $"replacement-label-{index}"
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

	static void ForceFullGc()
	{
		for (var i = 0; i < 5; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(25);
		}
	}
}

internal sealed class Payload
{
	readonly byte[] _bytes;

	public Payload(int index, int byteCount)
	{
		Index = index;
		_bytes = new byte[byteCount];
		_bytes[0] = (byte)(index % 251);
		_bytes[^1] = (byte)((index + 17) % 251);
	}

	public int Index { get; }
}

internal sealed record ScenarioResult(
	string Name,
	int PayloadsAlive,
	int ScrollParentsAlive,
	int RenderersAlive,
	int CachedContentsWithParent);

internal sealed record ReproReport(
	int Iterations,
	int PayloadBytes,
	ScenarioResult ReplacementWithoutRendererControl,
	ScenarioResult ReplacedContent,
	bool LeakProved)
{
	public string ToText()
	{
		var payloadMiB = PayloadBytes / 1024.0 / 1024.0;
		var retainedMiB = ReplacedContent.PayloadsAlive * payloadMiB;

		return string.Join(Environment.NewLine, new[]
		{
			"SwipeViewRenderer content replacement leak repro",
			$"Iterations: {Iterations}",
			$"Payload per parent ScrollView: {payloadMiB:F1} MiB",
			"",
			FormatScenario(ReplacementWithoutRendererControl),
			FormatScenario(ReplacedContent),
			"",
			$"Retained payload in suspect scenario: {retainedMiB:F1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}"
		});
	}

	static string FormatScenario(ScenarioResult result)
	{
		return string.Join(Environment.NewLine, new[]
		{
			$"Scenario: {result.Name}",
			$"  Alive payloads: {result.PayloadsAlive}",
			$"  Alive parent ScrollViews: {result.ScrollParentsAlive}",
			$"  Alive disposed renderers: {result.RenderersAlive}",
			$"  Cached contents still parented: {result.CachedContentsWithParent}"
		});
	}
}
