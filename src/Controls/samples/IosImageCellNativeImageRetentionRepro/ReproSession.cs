#nullable enable

#pragma warning disable CA1422 // Repro intentionally exercises legacy UITableViewCell.ImageView used by ImageCellRenderer.

using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Platform;
using UIKit;

namespace IosImageCellNativeImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 240;
	internal const int SourceImagePixels = 256;

	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-imagecell-native-image-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native cell image before disconnect",
			context,
			clearNativeImageAndDisposeResult: true);

		var current = await RunScenarioAsync(
			"current: MAUI ImageCellRenderer leaves native cell image assigned",
			context,
			clearNativeImageAndDisposeResult: false);

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
#pragma warning disable CS0618
		var imageCell = new ImageCell
		{
			Text = $"Item {cycle:000}",
			ImageSource = source
		};
#pragma warning restore CS0618
		ImageCellRenderer? renderer = null;

		var platformCell = clearNativeImageAndDisposeResult
			? await CreateCellWithDisposedResultAsync(imageCell, source, context)
			: await CreateCellWithCurrentRendererAsync(imageCell, renderer = new ImageCellRenderer(), context);

		if (platformCell.ImageView?.Image is null)
			throw new InvalidOperationException("ImageCellRenderer did not assign a native UIImage.");

		if (clearNativeImageAndDisposeResult)
			platformCell.ImageView.Image = null;

		if (renderer is not null)
			((IElementHandler)renderer).DisconnectHandler();

		platformCell.Cell = null;
		imageCell.ImageSource = null;
		imageCell.Handler = null;
		await DrainMainQueueAsync();

		return new CycleResult(
			new RetainedPeer(platformCell),
			TrackedCycle.Create(cycle, platformCell, imageCell, renderer, source));
	}

#pragma warning disable CS0618
	static async Task<CellTableViewCell> CreateCellWithCurrentRendererAsync(
		ImageCell imageCell,
		ImageCellRenderer renderer,
		IMauiContext context)
#pragma warning restore CS0618
	{
		AttachHandler(imageCell, renderer, context);
		var platformCell = (CellTableViewCell)renderer.PlatformView;
		await DrainMainQueueAsync();
		return platformCell;
	}

#pragma warning disable CS0618
	static async Task<CellTableViewCell> CreateCellWithDisposedResultAsync(
		ImageCell imageCell,
		TrackingImageSource source,
		IMauiContext context)
	{
		var platformCell = new CellTableViewCell(UITableViewCellStyle.Default, typeof(ImageCell).FullName)
		{
			Cell = imageCell
		};
#pragma warning restore CS0618

		var provider = context.Services.GetRequiredService<IImageSourceServiceProvider>();
		var service = provider.GetRequiredImageSourceService(source);
		var result = await service.GetImageAsync(source, scale: 1);

		try
		{
			var image = result?.Value ?? throw new InvalidOperationException("Image source service returned no image.");
			platformCell.ImageView.Image = image;
			platformCell.SetNeedsLayout();
			await DrainMainQueueAsync();
		}
		finally
		{
			result?.Dispose();
		}

		return platformCell;
	}

	static void AttachHandler(IElement view, IElementHandler handler, IMauiContext context)
	{
		handler.SetMauiContext(context);
		view.Handler = handler;
	}

	static async Task DrainMainQueueAsync()
	{
		await Task.Delay(50);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.02));
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

	internal sealed record RetainedPeer(CellTableViewCell Peer);

	internal sealed record CycleResult(RetainedPeer RetainedPeer, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<CellTableViewCell> NativePeer,
		WeakReference<object> VirtualView,
		WeakReference<IElementHandler>? Handler,
		WeakReference<TrackingImageSource> Source)
	{
		public static TrackedCycle Create(
			int cycle,
			CellTableViewCell platformView,
			object virtualView,
			IElementHandler? handler,
			TrackingImageSource source)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<CellTableViewCell>(platformView),
				new WeakReference<object>(virtualView),
				handler is null ? null : new WeakReference<IElementHandler>(handler),
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
			IReadOnlyList<RetainedPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var nativePeersWithAssignedImages = 0;
			long estimatedAssignedImageBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				if (retainedPeer.Peer.ImageView?.Image is UIImage image)
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

				if (cycle.Handler?.TryGetTarget(out _) == true)
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
			"IosImageCellNativeImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Cell image size: {SourceImagePixels} x {SourceImagePixels} pixels",
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
		var renderer = new UIGraphicsImageRenderer(new CGSize(ReproSession.SourceImagePixels, ReproSession.SourceImagePixels), format);

		return renderer.CreateImage(context =>
		{
			UIColor.FromRGB(
				(nfloat)((cycle * 41) % 255) / 255f,
				(nfloat)((cycle * 73) % 255) / 255f,
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
