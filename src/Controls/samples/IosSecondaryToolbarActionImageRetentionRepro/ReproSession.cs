#nullable enable

using System.Reflection;
using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using Microsoft.Maui.Handlers;
using UIKit;

namespace IosSecondaryToolbarActionImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 240;
	internal const int SourceImagePixels = 256;

	static readonly MethodInfo ToSecondarySubToolbarItemMethod =
		typeof(ToolbarItemExtensions).GetMethod("ToSecondarySubToolbarItem", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(ToolbarItemExtensions).FullName, "ToSecondarySubToolbarItem");

	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-secondary-toolbar-uiaction-image-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS secondary toolbar UIAction image retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear secondary toolbar action image before retaining peer",
			context,
			clearNativeImageAndDisposeResult: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: MAUI SecondarySubToolbarItem leaves action image assigned",
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
		var source = new TrackingFileImageSource(ledger, cycle);
		var toolbarItem = new ToolbarItem
		{
			Text = $"Menu {cycle:000}",
			IconImageSource = source,
			Order = ToolbarItemOrder.Secondary
		};

		var page = new ContentPage
		{
			Title = $"Page {cycle:000}"
		};
		page.ToolbarItems.Add(toolbarItem);

		var handler = new PageHandler();
		AttachHandler(page, handler, context);

		var nativeAction = clearNativeImageAndDisposeResult
			? await CreateActionWithDisposedResultAsync(toolbarItem, source, context)
			: await CreateActionWithCurrentPathAsync(toolbarItem);

		if (nativeAction.Image is null)
			throw new InvalidOperationException("Secondary toolbar action conversion did not assign a native UIImage.");

		if (clearNativeImageAndDisposeResult)
			nativeAction.Image = null;

		((IElementHandler)handler).DisconnectHandler();
		page.ToolbarItems.Clear();
		page.Handler = null;
		await DrainMainQueueAsync();

		return new CycleResult(
			new RetainedPeer(nativeAction),
			TrackedCycle.Create(cycle, nativeAction, page, toolbarItem, handler, source));
	}

	static async Task<UIAction> CreateActionWithCurrentPathAsync(ToolbarItem toolbarItem)
	{
		var wrapper = ToSecondarySubToolbarItemMethod.Invoke(null, new object[] { toolbarItem })
			?? throw new InvalidOperationException("ToSecondarySubToolbarItem returned null.");

		var platformActionProperty = wrapper.GetType().GetProperty("PlatformAction", BindingFlags.Instance | BindingFlags.Public)
			?? throw new MissingMemberException(wrapper.GetType().FullName, "PlatformAction");

		var nativeAction = (UIAction?)platformActionProperty.GetValue(wrapper)
			?? throw new InvalidOperationException("SecondarySubToolbarItem.PlatformAction returned null.");

		await DrainMainQueueAsync();
		return nativeAction;
	}

	static async Task<UIAction> CreateActionWithDisposedResultAsync(
		ToolbarItem toolbarItem,
		TrackingFileImageSource source,
		IMauiContext context)
	{
		var nativeAction = UIAction.Create(toolbarItem.Text, null, null, _ => { });

		var provider = context.Services.GetRequiredService<IImageSourceServiceProvider>();
		var service = provider.GetRequiredImageSourceService(source);
		var result = await service.GetImageAsync(source, scale: 1);

		try
		{
			nativeAction.Image = UIImage.FromFile(source.File) ?? throw new InvalidOperationException("File image source returned no image.");
			await DrainMainQueueAsync();
		}
		finally
		{
			result?.Dispose();
		}

		return nativeAction;
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

	internal sealed record RetainedPeer(UIAction Peer);

	internal sealed record CycleResult(RetainedPeer RetainedPeer, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<UIAction> NativePeer,
		WeakReference<Page> Page,
		WeakReference<ToolbarItem> ToolbarItem,
		WeakReference<IElementHandler> Handler,
		WeakReference<TrackingFileImageSource> Source)
	{
		public static TrackedCycle Create(
			int cycle,
			UIAction nativeAction,
			Page page,
			ToolbarItem toolbarItem,
			IElementHandler handler,
			TrackingFileImageSource source)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UIAction>(nativeAction),
				new WeakReference<Page>(page),
				new WeakReference<ToolbarItem>(toolbarItem),
				new WeakReference<IElementHandler>(handler),
				new WeakReference<TrackingFileImageSource>(source));
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
		int AlivePages,
		int AliveToolbarItems,
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
				if (retainedPeer.Peer.Image is UIImage image)
				{
					nativePeersWithAssignedImages++;
					estimatedAssignedImageBytes += EstimateImageBytes(image);
				}
			}

			var aliveNativePeers = 0;
			var alivePages = 0;
			var aliveToolbarItems = 0;
			var aliveHandlers = 0;
			var aliveSources = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.Page.TryGetTarget(out _))
					alivePages++;

				if (cycle.ToolbarItem.TryGetTarget(out _))
					aliveToolbarItems++;

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
				alivePages,
				aliveToolbarItems,
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
		Current.EstimatedAssignedImageBytes > Control.EstimatedAssignedImageBytes &&
		Current.AlivePages <= 1 &&
		Current.AliveToolbarItems <= 1 &&
		Current.AliveHandlers <= 1 &&
		Current.AliveSources <= 1;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosSecondaryToolbarActionImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Source image size: {SourceImagePixels} x {SourceImagePixels} pixels",
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
			$"  alive pages: {result.AlivePages}/{result.TrackedCycles}",
			$"  alive toolbar items: {result.AliveToolbarItems}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveSources}/{result.TrackedCycles}");
	}
}

internal sealed class TrackingFileImageSource : ImageSource, IFileImageSource
{
	public TrackingFileImageSource(ScenarioLedger ledger, int cycle)
	{
		Ledger = ledger;
		Cycle = cycle;
		File = CreateImageFile(cycle);
	}

	public ScenarioLedger Ledger { get; }

	public int Cycle { get; }

	public string File { get; }

	public override bool IsEmpty => false;

	static string CreateImageFile(int cycle)
	{
		var path = Path.Combine(Path.GetTempPath(), $"ios-secondary-toolbar-uiaction-image-{cycle:000}.png");
		using var image = TrackingImageSourceService.CreateImage(cycle);
		using var data = image.AsPNG() ?? throw new InvalidOperationException("Could not create PNG data.");
		using var url = NSUrl.FromFilename(path);
		if (!data.Save(url, true))
			throw new InvalidOperationException($"Could not write image file {path}.");

		return path;
	}
}

internal sealed class TrackingImageSourceService : ImageSourceService, IImageSourceService<TrackingFileImageSource>
{
	public override Task<IImageSourceServiceResult<UIImage>?> GetImageAsync(
		IImageSource imageSource,
		float scale = 1,
		CancellationToken cancellationToken = default)
	{
		if (imageSource is not TrackingFileImageSource trackingSource)
			return Task.FromResult<IImageSourceServiceResult<UIImage>?>(null);

		var image = CreateImage(trackingSource.Cycle);
		trackingSource.Ledger.RecordCreated();

		var result = new ImageSourceServiceResult(
			image,
			dispose: trackingSource.Ledger.RecordDisposed);

		return Task.FromResult<IImageSourceServiceResult<UIImage>?>(result);
	}

	internal static UIImage CreateImage(int cycle)
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
