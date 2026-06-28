#nullable enable

using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using UIKit;

namespace PhoneFlyoutPageBackgroundPatternRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 20;
	internal const int SourceImagePixels = 768;

	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "phoneflyoutpage-background-pattern-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting PhoneFlyoutPageRenderer background pattern retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native pattern background before retaining peer",
			context,
			clearNativeBackground: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: MAUI dispose leaves native pattern background assigned",
			context,
			clearNativeBackground: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			SourceImagePixels,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeBackground)
	{
		var ledger = new ScenarioLedger(name);
		var retainedPeers = new List<RetainedPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 5 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var result = await RunCycleAsync(i, ledger, context, clearNativeBackground);
			retainedPeers.Add(result.RetainedPeer);
			tracked.Add(result.Tracked);
		}

		RetainedNativePeers.AddRange(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, ledger, retainedPeers, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		ScenarioLedger ledger,
		IMauiContext context,
		bool clearNativeBackground)
	{
		var source = new TrackingImageSource(ledger, cycle);
		var flyoutPage = new PayloadFlyoutPage(cycle, source);
		var renderer = new PhoneFlyoutPageRenderer();
		((IElementHandler)renderer).SetMauiContext(context);

		renderer.SetElement(flyoutPage);
		renderer.LoadViewIfNeeded();
		await DrainMainQueueAsync();

		var nativePeer = renderer.View ?? throw new InvalidOperationException("PhoneFlyoutPageRenderer did not create a UIView.");

		if (!HasPatternBackground(nativePeer))
			throw new InvalidOperationException("PhoneFlyoutPageRenderer did not assign a pattern-image background.");

		renderer.Dispose();
		((IElementController)flyoutPage).EffectControlProvider = null;
		flyoutPage.Handler = null;
		flyoutPage.Flyout.Handler = null;
		flyoutPage.Detail.Handler = null;

		if (clearNativeBackground)
			nativePeer.BackgroundColor = UIColor.White;

		return new CycleResult(
			new RetainedPeer(nativePeer),
			TrackedCycle.Create(cycle, nativePeer, renderer, flyoutPage, source));
	}

	internal static async Task DrainMainQueueAsync()
	{
		await Task.Delay(20);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.005));
	}

	static bool HasPatternBackground(UIView view)
	{
		var color = view.BackgroundColor;
		if (color is null)
			return false;

		nfloat red;
		nfloat green;
		nfloat blue;
		nfloat alpha;

		try
		{
			color.GetRGBA(out red, out green, out blue, out alpha);
		}
		catch
		{
			return true;
		}

		const double tolerance = 0.001;
		return Math.Abs(red - 1) > tolerance ||
			Math.Abs(green - 1) > tolerance ||
			Math.Abs(blue - 1) > tolerance ||
			Math.Abs(alpha - 1) > tolerance;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(100);
		}
	}

	static void WriteProgress(string message)
	{
		try
		{
			File.WriteAllText(ResultsPath, message + Environment.NewLine);
		}
		catch
		{
			// Progress output is diagnostic only; the final report write remains authoritative.
		}
	}

	internal sealed record RetainedPeer(UIView Peer);

	internal sealed record CycleResult(RetainedPeer RetainedPeer, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<UIView> NativePeer,
		WeakReference<PhoneFlyoutPageRenderer> Renderer,
		WeakReference<FlyoutPage> FlyoutPage,
		WeakReference<TrackingImageSource> Source)
	{
		public static TrackedCycle Create(
			int cycle,
			UIView nativePeer,
			PhoneFlyoutPageRenderer renderer,
			FlyoutPage flyoutPage,
			TrackingImageSource source)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UIView>(nativePeer),
				new WeakReference<PhoneFlyoutPageRenderer>(renderer),
				new WeakReference<FlyoutPage>(flyoutPage),
				new WeakReference<TrackingImageSource>(source));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ServiceResultsCreated,
		int ServiceResultsDisposed,
		int RetainedNativePeers,
		int NativePeersWithPatternBackground,
		long EstimatedPatternImageBytes,
		int AliveNativePeers,
		int AliveRenderers,
		int AliveFlyoutPages,
		int AliveSources)
	{
		internal static ScenarioResult From(
			string name,
			ScenarioLedger ledger,
			IReadOnlyList<RetainedPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var nativePeersWithPatternBackground = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				if (HasPatternBackground(retainedPeer.Peer))
					nativePeersWithPatternBackground++;
			}

			var aliveNativePeers = 0;
			var aliveRenderers = 0;
			var aliveFlyoutPages = 0;
			var aliveSources = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.FlyoutPage.TryGetTarget(out _))
					aliveFlyoutPages++;

				if (cycle.Source.TryGetTarget(out _))
					aliveSources++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				ledger.ResultsCreated,
				ledger.ResultsDisposed,
				retainedPeers.Count,
				nativePeersWithPatternBackground,
				nativePeersWithPatternBackground * SourceImagePixels * SourceImagePixels * 4L,
				aliveNativePeers,
				aliveRenderers,
				aliveFlyoutPages,
				aliveSources);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int SourceImagePixels,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.ServiceResultsCreated >= Cycles &&
		Control.RetainedNativePeers == Cycles &&
		Control.NativePeersWithPatternBackground == 0 &&
		Current.ServiceResultsCreated >= Cycles &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativePeersWithPatternBackground == Cycles &&
		Current.EstimatedPatternImageBytes >= Cycles * SourceImagePixels * SourceImagePixels * 4L &&
		Current.AliveFlyoutPages == 0 &&
		Current.AliveSources == 0;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedPatternImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedPatternImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"PhoneFlyoutPageBackgroundPatternRetentionRepro",
			$"Cycles: {Cycles}",
			$"Background image size: {SourceImagePixels} x {SourceImagePixels} pixels",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native pattern image payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native pattern image payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeImageMiB = result.EstimatedPatternImageBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  service results created/disposed: {result.ServiceResultsCreated}/{result.ServiceResultsDisposed}",
			$"  retained native peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native peers with pattern background: {result.NativePeersWithPatternBackground}/{result.TrackedCycles}",
			$"  estimated assigned native image bytes: {result.EstimatedPatternImageBytes:N0}",
			$"  estimated assigned native image MiB: {nativeImageMiB:N1}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive flyout pages: {result.AliveFlyoutPages}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveSources}/{result.TrackedCycles}");
	}
}

sealed class PayloadFlyoutPage : FlyoutPage
{
	public PayloadFlyoutPage(int cycle, ImageSource backgroundSource)
	{
		Title = $"Regional operations {cycle + 1}";
		AutomationId = $"phone-flyout-background-pattern-{cycle + 1}";
		BackgroundImageSource = backgroundSource;
		FlyoutLayoutBehavior = FlyoutLayoutBehavior.Popover;

		Flyout = new ContentPage
		{
			Title = $"Routes {cycle + 1}",
			Content = new VerticalStackLayout
			{
				Children =
				{
					new Label { Text = "Dispatch" },
					new Label { Text = $"Region {cycle + 1}" }
				}
			}
		};

		Detail = new ContentPage
		{
			Title = $"Board {cycle + 1}",
			Content = new Grid
			{
				Children =
				{
					new Label { Text = $"Regional dispatch board {cycle + 1}" }
				}
			}
		};
	}
}

internal sealed class TrackingImageSource : ImageSource
{
	public TrackingImageSource(ScenarioLedger ledger, int cycle)
	{
		Ledger = ledger;
		Cycle = cycle;
	}

	public ScenarioLedger Ledger { get; }

	public int Cycle { get; }

	public override bool IsEmpty => false;
}

internal sealed class TrackingImageSourceService : ImageSourceService, IImageSourceService<TrackingImageSource>
{
	public override Task<IImageSourceServiceResult<UIImage>?> GetImageAsync(
		IImageSource imageSource,
		float scale = 1,
		CancellationToken cancellationToken = default)
	{
		if (imageSource is not TrackingImageSource trackingSource)
			return Task.FromResult<IImageSourceServiceResult<UIImage>?>(null);

		var image = CreateImage(trackingSource.Cycle);
		trackingSource.Ledger.RecordCreated();

		var result = new ImageSourceServiceResult(
			image,
			dispose: trackingSource.Ledger.RecordDisposed);

		return Task.FromResult<IImageSourceServiceResult<UIImage>?>(result);
	}

	static UIImage CreateImage(int cycle)
	{
		var format = new UIGraphicsImageRendererFormat
		{
			Opaque = true,
			Scale = 1
		};
		var renderer = new UIGraphicsImageRenderer(new CGSize(ReproSession.SourceImagePixels, ReproSession.SourceImagePixels), format);

		return renderer.CreateImage(context =>
		{
			UIColor.FromRGB(
				(nfloat)((cycle * 29) % 255) / 255f,
				(nfloat)((cycle * 67) % 255) / 255f,
				(nfloat)((cycle * 109) % 255) / 255f).SetFill();
			context.FillRect(new CGRect(0, 0, ReproSession.SourceImagePixels, ReproSession.SourceImagePixels));
		});
	}
}

internal sealed class ScenarioLedger
{
	public ScenarioLedger(string name)
	{
		Name = name;
	}

	public string Name { get; }

	public int ResultsCreated { get; private set; }

	public int ResultsDisposed { get; private set; }

	public void RecordCreated() => ResultsCreated++;

	public void RecordDisposed() => ResultsDisposed++;
}
