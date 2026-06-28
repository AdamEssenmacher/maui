#nullable enable

using System.Threading;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;
using MauiContentView = Microsoft.Maui.Controls.ContentView;
using PlatformContentView = Microsoft.Maui.Platform.ContentView;

namespace IosBackgroundImageLayerRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 120;
	internal const int SourceImagePixels = 512;
	const string BackgroundLayerName = "MauiBackgroundLayer";

	static readonly PropertyMapper<IContentView, IContentViewHandler> EmptyContentViewMapper = new();
	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "ios-background-image-layer-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: remove background layer before disconnect",
			context,
			removeBackgroundLayerAndDisposeResult: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves background image layer assigned",
			context,
			removeBackgroundLayerAndDisposeResult: false);

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
		bool removeBackgroundLayerAndDisposeResult)
	{
		var ledger = new ScenarioLedger(name);
		var retainedPeers = new List<RetainedPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			using var pool = new NSAutoreleasePool();

			var source = new TrackingImageSource(ledger, i);
			var contentView = new MauiContentView
			{
				WidthRequest = 320,
				HeightRequest = 180
			};
			var handler = new ContentViewHandler(EmptyContentViewMapper);

			AttachHandler(contentView, handler, context);

			var platformView = handler.PlatformView;
			platformView.Frame = new CGRect(0, 0, 320, 180);
			var provider = handler.GetRequiredService<IImageSourceServiceProvider>();

			if (removeBackgroundLayerAndDisposeResult)
			{
				await UpdateBackgroundImageSourceAndDisposeResultAsync(platformView, source, provider);
			}
			else
			{
				await platformView.UpdateBackgroundImageSourceAsync(source, provider);
			}

			if (!HasBackgroundLayerWithContents(platformView))
				throw new InvalidOperationException("ContentView did not assign a native background image layer.");

			if (removeBackgroundLayerAndDisposeResult)
				platformView.RemoveBackgroundLayer();

			((IElementHandler)handler).DisconnectHandler();
			contentView.Handler = null;

			retainedPeers.Add(new RetainedPeer(platformView));
			tracked.Add(TrackedCycle.Create(i, platformView, contentView, handler, source));
		}

		RetainedNativePeers.AddRange(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, ledger, retainedPeers, tracked);
	}

	static async Task UpdateBackgroundImageSourceAndDisposeResultAsync(
		UIView platformView,
		IImageSource imageSource,
		IImageSourceServiceProvider provider)
	{
		platformView.RemoveBackgroundLayer();

		var service = provider.GetRequiredImageSourceService(imageSource);
		var result = await service.GetImageAsync(imageSource, scale: 1);

		try
		{
			var backgroundImage = result?.Value;
			var cgImage = backgroundImage?.CGImage;

			if (cgImage is null)
				throw new InvalidOperationException("Image source service did not produce a CGImage-backed UIImage.");

			var imageLayer = new CALayer
			{
				Name = BackgroundLayerName,
				Contents = cgImage,
				Frame = platformView.Bounds,
				ContentsGravity = CALayer.GravityResize
			};

			platformView.BackgroundColor = UIColor.Clear;
			platformView.InsertBackgroundLayer(imageLayer, 0);
		}
		finally
		{
			result?.Dispose();
		}
	}

	static void AttachHandler(IElement view, IElementHandler handler, IMauiContext context)
	{
		handler.SetMauiContext(context);
		view.Handler = handler;
	}

	static bool HasBackgroundLayerWithContents(UIView view) =>
		GetBackgroundLayer(view)?.Contents is not null;

	static CALayer? GetBackgroundLayer(UIView view)
	{
		var layer = view.Layer;

		if (layer.Name == BackgroundLayerName)
			return layer;

		var sublayers = layer.Sublayers;
		if (sublayers is null)
			return null;

		foreach (var sublayer in sublayers)
		{
			if (sublayer.Name == BackgroundLayerName)
				return sublayer;
		}

		return null;
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

	internal sealed record RetainedPeer(PlatformContentView Peer);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<PlatformContentView> NativePeer,
		WeakReference<object> VirtualView,
		WeakReference<IElementHandler> Handler,
		WeakReference<TrackingImageSource> Source)
	{
		public static TrackedCycle Create(
			int cycle,
			PlatformContentView platformView,
			object virtualView,
			IElementHandler handler,
			TrackingImageSource source)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<PlatformContentView>(platformView),
				new WeakReference<object>(virtualView),
				new WeakReference<IElementHandler>(handler),
				new WeakReference<TrackingImageSource>(source));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ServiceResultsCreated,
		int ServiceResultsDisposed,
		int RetainedNativePeers,
		int NativePeersWithBackgroundLayerContents,
		long EstimatedAssignedImageBytes,
		int AliveNativePeers,
		int AliveVirtualViews,
		int AliveHandlers,
		int AliveSources)
	{
		internal static ScenarioResult From(
			string name,
			ScenarioLedger ledger,
			IReadOnlyList<RetainedPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var nativePeersWithLayerContents = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				if (HasBackgroundLayerWithContents(retainedPeer.Peer))
					nativePeersWithLayerContents++;
			}

			var aliveNativePeers = 0;
			var aliveVirtualViews = 0;
			var aliveHandlers = 0;
			var aliveSources = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;

				if (cycle.Source.TryGetTarget(out _))
					aliveSources++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				ledger.ResultsCreated,
				ledger.ResultsDisposed,
				retainedPeers.Count,
				nativePeersWithLayerContents,
				nativePeersWithLayerContents * SourceImagePixels * SourceImagePixels * 4L,
				aliveNativePeers,
				aliveVirtualViews,
				aliveHandlers,
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
		Control.NativePeersWithBackgroundLayerContents == 0 &&
		Current.ServiceResultsCreated == Cycles &&
		Current.ServiceResultsDisposed == 0 &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativePeersWithBackgroundLayerContents == Cycles &&
		Current.EstimatedAssignedImageBytes >= Cycles * SourceImagePixels * SourceImagePixels * 4L &&
		Current.AliveVirtualViews == 0 &&
		Current.AliveHandlers == 0 &&
		Current.AliveSources == 0;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosBackgroundImageLayerRetentionRepro",
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
			$"Control estimated assigned native background image payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native background image payload: {retainedMiB:N1} MiB",
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
			$"  native peers with background layer contents: {result.NativePeersWithBackgroundLayerContents}/{result.TrackedCycles}",
			$"  estimated assigned native image bytes: {result.EstimatedAssignedImageBytes:N0}",
			$"  estimated assigned native image MiB: {nativeImageMiB:N1}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive virtual views: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
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
				(nfloat)((cycle * 31) % 255) / 255f,
				(nfloat)((cycle * 71) % 255) / 255f,
				(nfloat)((cycle * 113) % 255) / 255f).SetFill();
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
