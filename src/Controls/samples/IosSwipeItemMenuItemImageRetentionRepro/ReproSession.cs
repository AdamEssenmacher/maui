#nullable enable

using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using UIKit;
using MauiSwipeItem = Microsoft.Maui.Controls.SwipeItem;

namespace IosSwipeItemMenuItemImageRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 240;

	static readonly PropertyMapper<ISwipeItemMenuItem, ISwipeItemMenuItemHandler> EmptyMapper = new();
	static readonly List<UIButton> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "ios-swipeitemmenuitem-image-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear UIButton image and reset SourceLoader before disconnect",
			context,
			clearNativeImageAndResetLoader: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves UIButton image and SourceLoader result assigned",
			context,
			clearNativeImageAndResetLoader: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeImageAndResetLoader)
	{
		var ledger = new ScenarioLedger(name);
		var retainedPeers = new List<UIButton>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
			await CreateCycleAsync(context, ledger, i, retainedPeers, tracked, clearNativeImageAndResetLoader);

		RetainedNativePeers.AddRange(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, ledger, retainedPeers, tracked);
	}

	static async Task CreateCycleAsync(
		IMauiContext context,
		ScenarioLedger ledger,
		int cycle,
		List<UIButton> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeImageAndResetLoader)
	{
		using var pool = new NSAutoreleasePool();

		var source = new TrackingImageSource(ledger, cycle);
		var item = new MauiSwipeItem
		{
			Text = $"Archive item {cycle:000}",
			IconImageSource = source,
			BackgroundColor = Colors.DarkBlue
		};
		var handler = new TestSwipeItemMenuItemHandler(EmptyMapper);

		AttachHandler(item, handler, context);
		handler.PlatformView.Frame = new CGRect(0, 0, 320, 256);

		await SwipeItemMenuItemHandler.MapSourceAsync(handler, item);

		var platformView = handler.PlatformView;
		if (platformView.ImageForState(UIControlState.Normal) is null)
			throw new InvalidOperationException("SwipeItemMenuItem did not assign a UIButton image.");

		if (clearNativeImageAndResetLoader)
		{
			platformView.SetImage(null, UIControlState.Normal);
			handler.SourceLoader.Reset();
		}

		((IElementHandler)handler).DisconnectHandler();
		item.IconImageSource = null;
		item.Handler = null;

		retainedPeers.Add(platformView);
		tracked.Add(TrackedCycle.Create(cycle, platformView, item, handler, source));
	}

	static void AttachHandler(IElement view, IElementHandler handler, IMauiContext context)
	{
		handler.SetMauiContext(context);
		view.Handler = handler;
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

	sealed class TestSwipeItemMenuItemHandler : SwipeItemMenuItemHandler
	{
		public TestSwipeItemMenuItemHandler(IPropertyMapper mapper)
			: base(mapper)
		{
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<UIButton> NativePeer,
		WeakReference<object> VirtualView,
		WeakReference<IElementHandler> Handler,
		WeakReference<TrackingImageSource> Source)
	{
		public static TrackedCycle Create(
			int cycle,
			UIButton platformView,
			object virtualView,
			IElementHandler handler,
			TrackingImageSource source)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UIButton>(platformView),
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
		int NativePeersWithAssignedImages,
		long EstimatedAssignedImageBytes,
		int AliveNativePeers,
		int AliveVirtualViews,
		int AliveHandlers,
		int AliveSources)
	{
		internal static ScenarioResult From(
			string name,
			ScenarioLedger ledger,
			IReadOnlyList<UIButton> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var nativePeersWithAssignedImages = 0;
			long estimatedAssignedImageBytes = 0;

			foreach (var peer in retainedPeers)
			{
				if (peer.ImageForState(UIControlState.Normal) is UIImage image)
				{
					nativePeersWithAssignedImages++;
					estimatedAssignedImageBytes += EstimateImageBytes(image);
				}
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
				nativePeersWithAssignedImages,
				estimatedAssignedImageBytes,
				aliveNativePeers,
				aliveVirtualViews,
				aliveHandlers,
				aliveSources);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
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
		Current.AliveVirtualViews == 0 &&
		Current.AliveHandlers == 0 &&
		Current.AliveSources == 0 &&
		Current.EstimatedAssignedImageBytes > 0;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosSwipeItemMenuItemImageRetentionRepro",
			$"Cycles: {Cycles}",
			"Assigned image target size: 128 x 128 pixels",
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
		var renderer = new UIGraphicsImageRenderer(new CGSize(512, 512), format);

		return renderer.CreateImage(context =>
		{
			UIColor.FromRGB(
				(nfloat)((cycle * 37) % 255) / 255f,
				(nfloat)((cycle * 67) % 255) / 255f,
				(nfloat)((cycle * 97) % 255) / 255f).SetFill();
			context.FillRect(new CGRect(0, 0, 512, 512));
		});
	}
}

internal sealed class ScenarioLedger
{
	readonly string _name;

	public ScenarioLedger(string name)
	{
		_name = name;
	}

	public string Name => _name;

	public int ResultsCreated { get; private set; }

	public int ResultsDisposed { get; private set; }

	public void RecordCreated() => ResultsCreated++;

	public void RecordDisposed() => ResultsDisposed++;
}
