#nullable enable

using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Handlers;
using UIKit;

namespace IosTabBarItemNativeImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 240;
	internal const int SourceImagePixels = 256;

	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-tabbaritem-native-image-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS TabBarItem native image retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native tab bar item image before retaining peer",
			context,
			clearNativeImageAndDisposeResult: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: MAUI TabbedRenderer leaves native tab bar item image assigned",
			context,
			clearNativeImageAndDisposeResult: false);

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
		bool clearNativeImageAndDisposeResult)
	{
		var ledger = new ScenarioLedger(name);
		var retainedPeers = new List<RetainedPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 25 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, ledger, context, clearNativeImageAndDisposeResult);
			retainedPeers.Add(cycleResult.RetainedPeer);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativePeers.AddRange(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, ledger, retainedPeers, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		ScenarioLedger ledger,
		IMauiContext context,
		bool clearNativeImageAndDisposeResult)
	{
		var source = new TrackingImageSource(ledger, cycle);
		var childPage = new ContentPage
		{
			Title = $"Operations {cycle:000}",
			IconImageSource = source
		};

		var tabbedPage = new TabbedPage
		{
			Title = $"Workspace {cycle:000}"
		};
		tabbedPage.Children.Add(childPage);

		var nativeItem = clearNativeImageAndDisposeResult
			? await CreateTabBarItemWithDisposedResultAsync(childPage, source, context)
			: await CreateTabBarItemWithCurrentPathAsync(tabbedPage, childPage, context);

		if (nativeItem.Image is null)
			throw new InvalidOperationException("TabbedRenderer did not assign a native UIImage.");

		if (clearNativeImageAndDisposeResult)
			nativeItem.Image = null;

		var trackedChildHandler = childPage.Handler as IElementHandler;

		if (trackedChildHandler is not null)
		{
			var childHandler = trackedChildHandler;
			childHandler.DisconnectHandler();
		}

		if (tabbedPage.Handler is IElementHandler tabbedHandler)
			tabbedHandler.DisconnectHandler();

		tabbedPage.Children.Clear();
		childPage.Handler = null;
		tabbedPage.Handler = null;
		await DrainMainQueueAsync();

		return new CycleResult(
			new RetainedPeer(nativeItem),
			TrackedCycle.Create(cycle, nativeItem, tabbedPage, childPage, trackedChildHandler, source));
	}

	static async Task<UITabBarItem> CreateTabBarItemWithCurrentPathAsync(
		TabbedPage tabbedPage,
		ContentPage childPage,
		IMauiContext context)
	{
#pragma warning disable CS0618
		var renderer = new TabbedRenderer();
#pragma warning restore CS0618

		((IElementHandler)renderer).SetMauiContext(context);
		((IElementHandler)renderer).SetVirtualView(tabbedPage);

		await DrainMainQueueAsync();

		if (childPage.Handler is not IPlatformViewHandler childHandler)
			throw new InvalidOperationException("TabbedRenderer did not create a child page handler.");

		var nativeItem = childHandler.ViewController?.TabBarItem;
		if (nativeItem is null)
			throw new InvalidOperationException("TabbedRenderer did not assign a UITabBarItem.");

		((IElementHandler)renderer).DisconnectHandler();

		return nativeItem;
	}

	static async Task<UITabBarItem> CreateTabBarItemWithDisposedResultAsync(
		Page page,
		TrackingImageSource source,
		IMauiContext context)
	{
		var provider = context.Services.GetRequiredService<IImageSourceServiceProvider>();
		var service = provider.GetRequiredImageSourceService(source);
		var result = await service.GetImageAsync(source, scale: 1);

		try
		{
			var image = result?.Value ?? throw new InvalidOperationException("Image source service returned no image.");
			var nativeItem = new UITabBarItem(page.Title, image, selectedImage: null)
			{
				AccessibilityIdentifier = page.AutomationId
			};

			await DrainMainQueueAsync();
			return nativeItem;
		}
		finally
		{
			result?.Dispose();
		}
	}

	static async Task DrainMainQueueAsync()
	{
		await Task.Delay(20);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.005));
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

	static long EstimateImageBytes(UIImage image)
	{
		var width = Math.Max(1, image.CGImage?.Width ?? (int)Math.Ceiling(image.Size.Width * image.CurrentScale));
		var height = Math.Max(1, image.CGImage?.Height ?? (int)Math.Ceiling(image.Size.Height * image.CurrentScale));
		return width * (long)height * 4;
	}

	internal sealed record RetainedPeer(UITabBarItem Peer);

	internal sealed record CycleResult(RetainedPeer RetainedPeer, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<UITabBarItem> NativePeer,
		WeakReference<object> TabbedPage,
		WeakReference<object> ChildPage,
		WeakReference? ChildHandler,
		WeakReference<TrackingImageSource> Source)
	{
		public static TrackedCycle Create(
			int cycle,
			UITabBarItem nativeItem,
			object tabbedPage,
			object childPage,
			IElementHandler? childHandler,
			TrackingImageSource source)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UITabBarItem>(nativeItem),
				new WeakReference<object>(tabbedPage),
				new WeakReference<object>(childPage),
				childHandler is null ? null : new WeakReference(childHandler),
				new WeakReference<TrackingImageSource>(source));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ServiceResultsCreated,
		int ServiceResultsDisposed,
		int RetainedNativePeers,
		int NativePeersWithAssignedImages,
		long EstimatedAssignedImageBytes,
		int AliveNativePeers,
		int AliveTabbedPages,
		int AliveChildPages,
		int AliveChildHandlers,
		int AliveSources)
	{
		internal static ScenarioResult From(
			string name,
			ScenarioLedger ledger,
			IReadOnlyList<RetainedPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var nativePeersWithAssignedImages = 0;
			long estimatedAssignedImageBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				if (retainedPeer.Peer.Image is UIImage image)
				{
					nativePeersWithAssignedImages++;
					estimatedAssignedImageBytes += EstimateImageBytes(image);
				}
			}

			var aliveNativePeers = 0;
			var aliveTabbedPages = 0;
			var aliveChildPages = 0;
			var aliveChildHandlers = 0;
			var aliveSources = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.TabbedPage.TryGetTarget(out _))
					aliveTabbedPages++;

				if (cycle.ChildPage.TryGetTarget(out _))
					aliveChildPages++;

				if (cycle.ChildHandler?.IsAlive == true)
					aliveChildHandlers++;

				if (cycle.Source.TryGetTarget(out _))
					aliveSources++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				ledger.ResultsCreated,
				ledger.ResultsDisposed,
				retainedPeers.Count,
				nativePeersWithAssignedImages,
				estimatedAssignedImageBytes,
				aliveNativePeers,
				aliveTabbedPages,
				aliveChildPages,
				aliveChildHandlers,
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
		Control.ServiceResultsCreated == Cycles &&
		Control.ServiceResultsDisposed == Cycles &&
		Control.RetainedNativePeers == Cycles &&
		Control.NativePeersWithAssignedImages == 0 &&
		Current.ServiceResultsCreated == Cycles &&
		Current.ServiceResultsDisposed == 0 &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativePeersWithAssignedImages == Cycles &&
		Current.EstimatedAssignedImageBytes > Control.EstimatedAssignedImageBytes &&
		Current.AliveTabbedPages == 0 &&
		Current.AliveChildPages == 0 &&
		Current.AliveChildHandlers == 0 &&
		Current.AliveSources == 0;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosTabBarItemNativeImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Source icon size: {SourceImagePixels} x {SourceImagePixels} pixels",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native image payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native image payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeImageMiB = result.EstimatedAssignedImageBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  service results created/disposed: {result.ServiceResultsCreated}/{result.ServiceResultsDisposed}",
			$"  retained native peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native peers with assigned UIImages: {result.NativePeersWithAssignedImages}/{result.TrackedCycles}",
			$"  estimated assigned native image bytes: {result.EstimatedAssignedImageBytes:N0}",
			$"  estimated assigned native image MiB: {nativeImageMiB:N1}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive tabbed pages: {result.AliveTabbedPages}/{result.TrackedCycles}",
			$"  alive child pages: {result.AliveChildPages}/{result.TrackedCycles}",
			$"  alive child handlers: {result.AliveChildHandlers}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveSources}/{result.TrackedCycles}");
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
				(nfloat)((cycle * 37) % 255) / 255f,
				(nfloat)((cycle * 83) % 255) / 255f,
				(nfloat)((cycle * 127) % 255) / 255f).SetFill();
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
