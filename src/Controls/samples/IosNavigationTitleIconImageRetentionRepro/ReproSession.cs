#nullable enable

using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using ObjCRuntime;
using UIKit;

namespace IosNavigationTitleIconImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 32;
	internal const int SourceImagePixels = 512;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly MethodInfo CreateViewControllerForPageMethod =
		typeof(NavigationRenderer).GetMethod("CreateViewControllerForPage", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(NavigationRenderer).FullName, "CreateViewControllerForPage");

	static readonly List<IReadOnlyList<RetainedPeer>> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-navigation-titleicon-image-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS NavigationPage title icon image retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear title icon UIImage before title container disconnect",
			context,
			clearNativeTitleIconBeforeDisconnect: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: title container disconnect leaves title icon UIImage assigned",
			context,
			clearNativeTitleIconBeforeDisconnect: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(Cycles, SourceImagePixels, baselineBytes, finalBytes, control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeTitleIconBeforeDisconnect)
	{
		var ledger = new ScenarioLedger();
		var retainedPeers = new List<RetainedPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 20 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CycleResult result;
			try
			{
				result = await RunCycleAsync(i, ledger, context, clearNativeTitleIconBeforeDisconnect);
			}
			catch (Exception ex)
			{
				WriteProgress($"{name}: cycle {i} failed: {ex}");
				throw;
			}

			retainedPeers.Add(result.RetainedPeer);
			tracked.Add(result.Tracked);
		}

		RetainedNativePeers.Add(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, ledger, retainedPeers, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		ScenarioLedger ledger,
		IMauiContext context,
		bool clearNativeTitleIconBeforeDisconnect)
	{
		var source = new TrackingImageSource(ledger, cycle);
		var rootPage = new ContentPage
		{
			Title = $"Root {cycle:000}",
			Content = new Label { Text = $"Root {cycle:000}" }
		};
		var navPage = new NavigationPage(rootPage);
		var renderer = new NavigationRenderer();

		((IElementHandler)renderer).SetMauiContext(context);
		renderer.SetElement(navPage);
		renderer.LoadViewIfNeeded();

		SetRealisticNavigationBounds(renderer);

		var titlePage = new ContentPage
		{
			Title = $"Orders {cycle:000}",
			Content = new Label { Text = $"Orders {cycle:000}" }
		};
		NavigationPage.SetTitleIconImageSource(titlePage, source);

		var pageController = CreateViewControllerForPage(renderer, titlePage);

		var titleContainer = await WaitForTitleContainerWithImageAsync(pageController);

		var retainedPeer = RetainNativePeer(titleContainer);

		if (clearNativeTitleIconBeforeDisconnect)
			ClearAssignedTitleIconImages(titleContainer);

		DisconnectPageController(pageController, dispose: false);

		if (titlePage.Handler is not null)
			titlePage.Handler.DisconnectHandler();

		renderer.Dispose();

		if (navPage.Handler == renderer)
			navPage.Handler = null;

		if (rootPage.Handler is not null)
			rootPage.Handler.DisconnectHandler();

		await DrainMainQueueAsync();

		return new CycleResult(
			retainedPeer,
			TrackedCycle.Create(cycle, renderer, navPage, rootPage, titlePage, source));
	}

	static UIViewController CreateViewControllerForPage(NavigationRenderer renderer, Page page)
	{
		return (UIViewController)(CreateViewControllerForPageMethod.Invoke(renderer, new object[] { page })
			?? throw new InvalidOperationException("NavigationRenderer did not create a page controller."));
	}

	static void DisconnectPageController(UIViewController pageController, bool dispose)
	{
		var method = pageController.GetType().GetMethod("Disconnect", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new MissingMethodException(pageController.GetType().FullName, "Disconnect");

		method.Invoke(pageController, new object[] { dispose });
	}

	static void SetRealisticNavigationBounds(NavigationRenderer renderer)
	{
		var view = renderer.View ?? throw new InvalidOperationException("NavigationRenderer did not create a UIView.");
		var navigationBar = renderer.NavigationBar ?? throw new InvalidOperationException("NavigationRenderer did not create a UINavigationBar.");
		var frame = new CGRect(0, 0, 390, 64);

		view.Frame = new CGRect(0, 0, 390, 844);
		view.Bounds = new CGRect(0, 0, 390, 844);
		navigationBar.Frame = frame;
		navigationBar.Bounds = frame;
	}

	static async Task<UIView> WaitForTitleContainerWithImageAsync(UIViewController pageController)
	{
		for (var i = 0; i < 100; i++)
		{
			using var pool = new NSAutoreleasePool();
			if (pageController.NavigationItem.TitleView is UIView titleView &&
				GetAssignedTitleIconImage(titleView) is not null)
			{
				return titleView;
			}

			await DrainMainQueueAsync();
		}

		throw new InvalidOperationException("NavigationRenderer did not assign a title icon UIImage.");
	}

	static async Task DrainMainQueueAsync()
	{
		await Task.Delay(25);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
	}

	static RetainedPeer RetainNativePeer(NSObject peer)
	{
		var handle = peer.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native title container with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedPeer(retained);
	}

	static UIImage? GetAssignedTitleIconImage(UIView view)
	{
		foreach (var subview in view.Subviews)
		{
			if (subview is UIImageView imageView && imageView.Image is UIImage image)
				return image;

			if (GetAssignedTitleIconImage(subview) is UIImage nestedImage)
				return nestedImage;
		}

		return null;
	}

	static void ClearAssignedTitleIconImages(UIView view)
	{
		foreach (var subview in view.Subviews)
		{
			if (subview is UIImageView imageView)
				imageView.Image = null;

			ClearAssignedTitleIconImages(subview);
		}
	}

	static long EstimateImageBytes(UIImage image)
	{
		var width = Math.Max(1, image.CGImage?.Width ?? (int)Math.Ceiling(image.Size.Width * image.CurrentScale));
		var height = Math.Max(1, image.CGImage?.Height ?? (int)Math.Ceiling(image.Size.Height * image.CurrentScale));
		return width * (long)height * 4;
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

	[DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

	internal sealed record RetainedPeer(IntPtr Handle)
	{
		public UIView? TryGetPeer()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UIView>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record CycleResult(RetainedPeer RetainedPeer, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<NavigationRenderer> Renderer,
		WeakReference<NavigationPage> NavigationPage,
		WeakReference<ContentPage> RootPage,
		WeakReference<ContentPage> TitlePage,
		WeakReference<TrackingImageSource> Source)
	{
		public static TrackedCycle Create(
			int cycle,
			NavigationRenderer renderer,
			NavigationPage navigationPage,
			ContentPage rootPage,
			ContentPage titlePage,
			TrackingImageSource source)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<NavigationRenderer>(renderer),
				new WeakReference<NavigationPage>(navigationPage),
				new WeakReference<ContentPage>(rootPage),
				new WeakReference<ContentPage>(titlePage),
				new WeakReference<TrackingImageSource>(source));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ServiceResultsCreated,
		int ServiceResultsDisposed,
		int RetainedNativePeers,
		int NativePeersWithAssignedTitleIconImages,
		long EstimatedAssignedImageBytes,
		int AliveRenderers,
		int AliveNavigationPages,
		int AliveRootPages,
		int AliveTitlePages,
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
				if (retainedPeer.TryGetPeer() is UIView titleContainer &&
					GetAssignedTitleIconImage(titleContainer) is UIImage image)
				{
					nativePeersWithAssignedImages++;
					estimatedAssignedImageBytes += EstimateImageBytes(image);
				}
			}

			var aliveRenderers = 0;
			var aliveNavigationPages = 0;
			var aliveRootPages = 0;
			var aliveTitlePages = 0;
			var aliveSources = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.NavigationPage.TryGetTarget(out _))
					aliveNavigationPages++;

				if (cycle.RootPage.TryGetTarget(out _))
					aliveRootPages++;

				if (cycle.TitlePage.TryGetTarget(out _))
					aliveTitlePages++;

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
				aliveRenderers,
				aliveNavigationPages,
				aliveRootPages,
				aliveTitlePages,
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
		Control.RetainedNativePeers == Cycles &&
		Control.NativePeersWithAssignedTitleIconImages == 0 &&
		Current.ServiceResultsCreated == Cycles &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativePeersWithAssignedTitleIconImages == Cycles &&
		Current.EstimatedAssignedImageBytes >= Cycles * SourceImagePixels * SourceImagePixels * 4L &&
		Current.AliveTitlePages == 0 &&
		Current.AliveSources == 0;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosNavigationTitleIconImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Title icon image size: {SourceImagePixels} x {SourceImagePixels} pixels",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native title icon payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native title icon payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeImageMiB = result.EstimatedAssignedImageBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  service results created/disposed: {result.ServiceResultsCreated}/{result.ServiceResultsDisposed}",
			$"  retained native title containers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native title containers with assigned UIImages: {result.NativePeersWithAssignedTitleIconImages}/{result.TrackedCycles}",
			$"  estimated assigned native title icon bytes: {result.EstimatedAssignedImageBytes:N0}",
			$"  estimated assigned native title icon MiB: {nativeImageMiB:N1}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive navigation pages: {result.AliveNavigationPages}/{result.TrackedCycles}",
			$"  alive root pages: {result.AliveRootPages}/{result.TrackedCycles}",
			$"  alive title pages: {result.AliveTitlePages}/{result.TrackedCycles}",
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
		var renderer = new UIGraphicsImageRenderer(
			new CGSize(ReproSession.SourceImagePixels, ReproSession.SourceImagePixels),
			format);

		return renderer.CreateImage(context =>
		{
			UIColor.FromRGB(
				(nfloat)((cycle * 37) % 255) / 255f,
				(nfloat)((cycle * 83) % 255) / 255f,
				(nfloat)((cycle * 127) % 255) / 255f).SetFill();
			context.FillRect(new CGRect(0, 0, ReproSession.SourceImagePixels, ReproSession.SourceImagePixels));

			UIColor.FromRGBA(1, 1, 1, 0.35f).SetFill();
			context.FillRect(new CGRect(
				(cycle * 13) % ReproSession.SourceImagePixels,
				(cycle * 29) % ReproSession.SourceImagePixels,
				ReproSession.SourceImagePixels / 3,
				ReproSession.SourceImagePixels / 3));
		});
	}
}

internal sealed class ScenarioLedger
{
	public int ResultsCreated { get; private set; }

	public int ResultsDisposed { get; private set; }

	public void RecordCreated() => ResultsCreated++;

	public void RecordDisposed() => ResultsDisposed++;
}
