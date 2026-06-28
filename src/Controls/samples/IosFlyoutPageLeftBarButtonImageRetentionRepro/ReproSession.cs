#nullable enable

#pragma warning disable CS0618

using System.Threading;
using System.Reflection;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Graphics;
using UIKit;

namespace IosFlyoutPageLeftBarButtonImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 120;
	internal const int SourceImagePixels = 256;
	internal const int PayloadBytes = 1024 * 1024;

	static readonly MethodInfo SetFlyoutLeftBarButtonMethod =
		typeof(NavigationRenderer).GetMethod("SetFlyoutLeftBarButton", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(NavigationRenderer).FullName, "SetFlyoutLeftBarButton");

	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-flyoutpage-leftbarbutton-image-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS FlyoutPage left bar button image retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native FlyoutPage left bar button image and avoid page-capturing action before retaining peer",
			context,
			clearNativeImageAndDisposeResult: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: MAUI NavigationRenderer leaves FlyoutPage left bar button action and image assigned",
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
		var payload = new PayloadViewModel(cycle);
		var flyoutPage = CreateFlyoutPage(source, payload, cycle);
		var flyoutHandler = AttachContext(flyoutPage, context);
		var flyoutContentHandler = AttachContext(flyoutPage.Flyout, context);
		var detailHandler = AttachContext(flyoutPage.Detail, context);

		var nativeBarButtonItem = clearNativeImageAndDisposeResult
			? await CreateBarButtonItemWithDisposedResultAsync(source, context)
			: await CreateBarButtonItemWithCurrentPathAsync(flyoutPage);

		if (nativeBarButtonItem.Image is null)
			throw new InvalidOperationException("NavigationRenderer did not assign a native UIImage.");

		flyoutPage.Flyout.IconImageSource = null;

		if (clearNativeImageAndDisposeResult)
			nativeBarButtonItem.Image = null;

		flyoutHandler.DisconnectHandler();
		flyoutContentHandler.DisconnectHandler();
		detailHandler.DisconnectHandler();
		await DrainMainQueueAsync();

		return new CycleResult(
			new RetainedPeer(nativeBarButtonItem),
			TrackedCycle.Create(
				cycle,
				nativeBarButtonItem,
				flyoutPage,
				flyoutPage.Flyout,
				flyoutPage.Detail,
				flyoutHandler,
				flyoutContentHandler,
				detailHandler,
				source,
				payload));
	}

	static FlyoutPage CreateFlyoutPage(ImageSource source, PayloadViewModel payload, int cycle)
	{
		return new FlyoutPage
		{
			Title = $"Operations {cycle:000}",
			BindingContext = payload,
			FlyoutLayoutBehavior = FlyoutLayoutBehavior.Popover,
			Flyout = new ContentPage
			{
				Title = $"Menu {cycle:000}",
				IconImageSource = source,
				Content = new Label { Text = "Menu" }
			},
			Detail = new ContentPage
			{
				Title = $"Detail {cycle:000}",
				Content = new Label { Text = "Detail" }
			}
		};
	}

	static ContextOnlyElementHandler AttachContext(IElement element, IMauiContext context)
	{
		var handler = new ContextOnlyElementHandler(context);
		handler.SetVirtualView(element);
		element.Handler = handler;
		return handler;
	}

	static async Task<UIBarButtonItem> CreateBarButtonItemWithCurrentPathAsync(FlyoutPage flyoutPage)
	{
		var viewController = new UIViewController();

		SetFlyoutLeftBarButtonMethod.Invoke(null, new object[] { viewController, flyoutPage });
		await DrainMainQueueAsync();

		var nativeBarButtonItem = viewController.NavigationItem.LeftBarButtonItem;
		if (nativeBarButtonItem is null)
			throw new InvalidOperationException("NavigationRenderer did not create LeftBarButtonItem.");

		viewController.NavigationItem.LeftBarButtonItem = null;
		nativeBarButtonItem.Target = null;
		nativeBarButtonItem.Action = null;
		viewController.Dispose();

		return nativeBarButtonItem;
	}

	static async Task<UIBarButtonItem> CreateBarButtonItemWithDisposedResultAsync(
		TrackingImageSource source,
		IMauiContext context)
	{
		var provider = context.Services.GetRequiredService<IImageSourceServiceProvider>();
		var service = provider.GetRequiredImageSourceService(source);
		var result = await service.GetImageAsync(source, scale: 1);

		try
		{
			var image = result?.Value ?? throw new InvalidOperationException("Image source service returned no image.");
			var nativeBarButtonItem = new UIBarButtonItem(image, UIBarButtonItemStyle.Plain, static (_, _) => { });
			nativeBarButtonItem.Target = null;
			nativeBarButtonItem.Action = null;
			await DrainMainQueueAsync();
			return nativeBarButtonItem;
		}
		finally
		{
			result?.Dispose();
		}
	}

	static async Task DrainMainQueueAsync()
	{
		await Task.Delay(30);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
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

	static long EstimateImageBytes(UIImage image)
	{
		var width = Math.Max(1, image.CGImage?.Width ?? (int)Math.Ceiling(image.Size.Width * image.CurrentScale));
		var height = Math.Max(1, image.CGImage?.Height ?? (int)Math.Ceiling(image.Size.Height * image.CurrentScale));
		return width * (long)height * 4;
	}

	internal sealed record RetainedPeer(UIBarButtonItem Peer);

	internal sealed record CycleResult(RetainedPeer RetainedPeer, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<UIBarButtonItem> NativePeer,
		WeakReference<FlyoutPage> FlyoutPage,
		WeakReference<Page> Flyout,
		WeakReference<Page> Detail,
		WeakReference<ContextOnlyElementHandler> FlyoutPageHandler,
		WeakReference<ContextOnlyElementHandler> FlyoutHandler,
		WeakReference<ContextOnlyElementHandler> DetailHandler,
		WeakReference<TrackingImageSource> Source,
		WeakReference<PayloadViewModel> Payload,
		WeakReference<byte[]> PayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			UIBarButtonItem nativeBarButtonItem,
			FlyoutPage flyoutPage,
			Page flyout,
			Page detail,
			ContextOnlyElementHandler flyoutPageHandler,
			ContextOnlyElementHandler flyoutHandler,
			ContextOnlyElementHandler detailHandler,
			TrackingImageSource source,
			PayloadViewModel payload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UIBarButtonItem>(nativeBarButtonItem),
				new WeakReference<FlyoutPage>(flyoutPage),
				new WeakReference<Page>(flyout),
				new WeakReference<Page>(detail),
				new WeakReference<ContextOnlyElementHandler>(flyoutPageHandler),
				new WeakReference<ContextOnlyElementHandler>(flyoutHandler),
				new WeakReference<ContextOnlyElementHandler>(detailHandler),
				new WeakReference<TrackingImageSource>(source),
				new WeakReference<PayloadViewModel>(payload),
				new WeakReference<byte[]>(payload.Payload));
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
		int AliveFlyoutPages,
		int AliveFlyouts,
		int AliveDetails,
		int AliveFlyoutPageHandlers,
		int AliveFlyoutHandlers,
		int AliveDetailHandlers,
		int AliveSources,
		int AlivePayloads,
		int AlivePayloadByteArrays,
		long AlivePayloadBytes)
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
			var aliveFlyoutPages = 0;
			var aliveFlyouts = 0;
			var aliveDetails = 0;
			var aliveFlyoutPageHandlers = 0;
			var aliveFlyoutHandlers = 0;
			var aliveDetailHandlers = 0;
			var aliveSources = 0;
			var alivePayloads = 0;
			var alivePayloadByteArrays = 0;
			long alivePayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.FlyoutPage.TryGetTarget(out _))
					aliveFlyoutPages++;

				if (cycle.Flyout.TryGetTarget(out _))
					aliveFlyouts++;

				if (cycle.Detail.TryGetTarget(out _))
					aliveDetails++;

				if (cycle.FlyoutPageHandler.TryGetTarget(out _))
					aliveFlyoutPageHandlers++;

				if (cycle.FlyoutHandler.TryGetTarget(out _))
					aliveFlyoutHandlers++;

				if (cycle.DetailHandler.TryGetTarget(out _))
					aliveDetailHandlers++;

				if (cycle.Source.TryGetTarget(out _))
					aliveSources++;

				if (cycle.Payload.TryGetTarget(out _))
					alivePayloads++;

				if (cycle.PayloadBytes.TryGetTarget(out var payloadBytes))
				{
					alivePayloadByteArrays++;
					alivePayloadBytes += payloadBytes.Length;
				}
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
				aliveFlyoutPages,
				aliveFlyouts,
				aliveDetails,
				aliveFlyoutPageHandlers,
				aliveFlyoutHandlers,
				aliveDetailHandlers,
				aliveSources,
				alivePayloads,
				alivePayloadByteArrays,
				alivePayloadBytes);
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
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.ServiceResultsCreated == Cycles &&
		Current.ServiceResultsDisposed == 0 &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativePeersWithAssignedImages == Cycles &&
		Current.EstimatedAssignedImageBytes > Control.EstimatedAssignedImageBytes &&
		Current.AliveFlyoutPages == Cycles &&
		Current.AliveFlyouts == Cycles &&
		Current.AliveDetails == Cycles &&
		Current.AliveFlyoutPageHandlers <= 1 &&
		Current.AliveFlyoutHandlers <= 1 &&
		Current.AliveDetailHandlers <= 1 &&
		Current.AliveSources <= 1 &&
		Current.AlivePayloads == Cycles &&
		Current.AlivePayloadByteArrays == Cycles;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var payloadMiB = Current.AlivePayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosFlyoutPageLeftBarButtonImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Source image size: {SourceImagePixels} x {SourceImagePixels} pixels",
			$"Payload per cycle: {ReproSession.PayloadBytes:N0} bytes",
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
			$"Current retained managed payload: {payloadMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeImageMiB = result.EstimatedAssignedImageBytes / 1024d / 1024d;
		var payloadMiB = result.AlivePayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  service results created/disposed: {result.ServiceResultsCreated}/{result.ServiceResultsDisposed}",
			$"  retained native peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native peers with assigned UIImages: {result.NativePeersWithAssignedImages}/{result.TrackedCycles}",
			$"  estimated assigned native image bytes: {result.EstimatedAssignedImageBytes:N0}",
			$"  estimated assigned native image MiB: {nativeImageMiB:N1}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive FlyoutPages: {result.AliveFlyoutPages}/{result.TrackedCycles}",
			$"  alive flyout pages: {result.AliveFlyouts}/{result.TrackedCycles}",
			$"  alive detail pages: {result.AliveDetails}/{result.TrackedCycles}",
			$"  alive FlyoutPage handlers: {result.AliveFlyoutPageHandlers}/{result.TrackedCycles}",
			$"  alive flyout handlers: {result.AliveFlyoutHandlers}/{result.TrackedCycles}",
			$"  alive detail handlers: {result.AliveDetailHandlers}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveSources}/{result.TrackedCycles}",
			$"  alive payloads: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  alive payload byte arrays: {result.AlivePayloadByteArrays}/{result.TrackedCycles}",
			$"  alive payload bytes: {result.AlivePayloadBytes:N0}",
			$"  alive payload MiB: {payloadMiB:N1}");
	}
}

internal sealed class ContextOnlyElementHandler : IViewHandler
{
	public ContextOnlyElementHandler(IMauiContext context)
	{
		MauiContext = context;
	}

	public object? PlatformView => null;

	public bool HasContainer { get; set; }

	public object? ContainerView => null;

	public IElement? VirtualView { get; private set; }

	IView? IViewHandler.VirtualView => VirtualView as IView;

	public IMauiContext? MauiContext { get; private set; }

	public void SetMauiContext(IMauiContext mauiContext)
	{
		MauiContext = mauiContext;
	}

	public void SetVirtualView(IElement view)
	{
		VirtualView = view;
	}

	public void UpdateValue(string property)
	{
	}

	public void Invoke(string command, object? args = null)
	{
	}

	public Size GetDesiredSize(double widthConstraint, double heightConstraint)
	{
		return Size.Zero;
	}

	public void PlatformArrange(Rect frame)
	{
	}

	public void DisconnectHandler()
	{
		if (VirtualView?.Handler == this)
			VirtualView.Handler = null;

		VirtualView = null;
		MauiContext = null;
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

internal sealed class PayloadViewModel
{
	public PayloadViewModel(int cycle)
	{
		Payload = new byte[ReproSession.PayloadBytes];
		Payload[0] = (byte)(cycle & 0xff);
		Payload[Payload.Length - 1] = (byte)((cycle * 17) & 0xff);
	}

	public byte[] Payload { get; }
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

#pragma warning restore CS0618
