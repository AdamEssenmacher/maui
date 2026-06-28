#nullable enable

using System.Reflection;
using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Graphics;
using UIKit;

namespace IosShellSearchBarIconImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 120;
	internal const int SourceImagePixels = 256;

	static readonly IconSpec[] IconSpecs =
	[
		new(UISearchBarIcon.Search, "Search"),
		new(UISearchBarIcon.Clear, "Clear"),
		new(UISearchBarIcon.Bookmark, "Bookmark")
	];

	static readonly UIControlState[] IconStates =
	[
		UIControlState.Normal,
		UIControlState.Highlighted,
		UIControlState.Selected
	];

	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-shell-searchbar-icon-image-retention-results.txt");

	public static int TotalIconLoads => Cycles * IconSpecs.Length;

	public static int TotalIconStateSlots => Cycles * IconSpecs.Length * IconStates.Length;

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS Shell search bar icon image retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear search bar icon images before retaining peer",
			context,
			clearNativeImagesAndDisposeResults: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: MAUI ShellPageRendererTracker leaves search bar icon images assigned",
			context,
			clearNativeImagesAndDisposeResults: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			SourceImagePixels,
			IconSpecs.Length,
			IconStates.Length,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeImagesAndDisposeResults)
	{
		var ledger = new ScenarioLedger(name);
		var retainedPeers = new List<RetainedPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 25 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, ledger, context, clearNativeImagesAndDisposeResults);
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
		bool clearNativeImagesAndDisposeResults)
	{
		var sources = IconSpecs
			.Select((_, index) => new TrackingImageSource(ledger, cycle, index))
			.ToArray();
		var sourceHandlers = sources
			.Select(source => AttachContext(source, context))
			.ToArray();
		var shell = new Shell { Title = $"Search Shell {cycle:000}" };
		var shellHandler = AttachContext(shell, context);

		var searchBar = clearNativeImagesAndDisposeResults
			? await CreateSearchBarWithDisposedResultsAsync(sources, context)
			: await CreateSearchBarWithCurrentPathAsync(shell, sources);

		if (CountAssignedIconStateSlots(searchBar) != IconSpecs.Length * IconStates.Length)
			throw new InvalidOperationException("Search bar icon assignment did not populate every expected icon state.");

		if (clearNativeImagesAndDisposeResults)
			ClearSearchBarIcons(searchBar);

		foreach (var handler in sourceHandlers)
			handler.DisconnectHandler();

		shellHandler.DisconnectHandler();
		await DrainMainQueueAsync();

		return new CycleResult(
			new RetainedPeer(searchBar),
			TrackedCycle.Create(cycle, searchBar, shell, shellHandler, sources, sourceHandlers));
	}

	static ContextOnlyElementHandler AttachContext(IElement element, IMauiContext context)
	{
		var handler = new ContextOnlyElementHandler(context);
		handler.SetVirtualView(element);
		element.Handler = handler;
		return handler;
	}

	static async Task<UISearchBar> CreateSearchBarWithCurrentPathAsync(
		Shell shell,
		IReadOnlyList<TrackingImageSource> sources)
	{
		var searchBar = new UISearchBar();
		var tracker = new TestShellPageRendererTracker(new FakeShellContext(shell));

		for (var i = 0; i < IconSpecs.Length; i++)
			tracker.SetSearchBarIconForTest(searchBar, sources[i], IconSpecs[i].Icon);

		await DrainMainQueueAsync();
		tracker.Dispose();

		return searchBar;
	}

	static async Task<UISearchBar> CreateSearchBarWithDisposedResultsAsync(
		IReadOnlyList<TrackingImageSource> sources,
		IMauiContext context)
	{
		var searchBar = new UISearchBar();
		var provider = context.Services.GetRequiredService<IImageSourceServiceProvider>();

		for (var i = 0; i < IconSpecs.Length; i++)
		{
			var service = provider.GetRequiredImageSourceService(sources[i]);
			var result = await service.GetImageAsync(sources[i], scale: 1);

			try
			{
				var image = result?.Value ?? throw new InvalidOperationException("Image source service returned no image.");
				var templatedImage = image.ImageWithRenderingMode(UIImageRenderingMode.AlwaysTemplate);

				foreach (var state in IconStates)
					searchBar.SetImageforSearchBarIcon(templatedImage, IconSpecs[i].Icon, state);
			}
			finally
			{
				result?.Dispose();
			}
		}

		await DrainMainQueueAsync();
		return searchBar;
	}

	static void ClearSearchBarIcons(UISearchBar searchBar)
	{
		foreach (var icon in IconSpecs)
		{
			foreach (var state in IconStates)
				searchBar.SetImageforSearchBarIcon(null!, icon.Icon, state);
		}
	}

	static int CountAssignedIconStateSlots(UISearchBar searchBar)
	{
		var count = 0;

		foreach (var icon in IconSpecs)
		{
			foreach (var state in IconStates)
			{
				if (searchBar.GetImageForSearchBarIcon(icon.Icon, state) is not null)
					count++;
			}
		}

		return count;
	}

	static long EstimateAssignedImageBytes(UISearchBar searchBar)
	{
		long bytes = 0;

		foreach (var icon in IconSpecs)
		{
			var image = searchBar.GetImageForSearchBarIcon(icon.Icon, UIControlState.Normal);
			if (image is not null)
				bytes += EstimateImageBytes(image);
		}

		return bytes;
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

	internal sealed record IconSpec(UISearchBarIcon Icon, string Name);

	internal sealed record RetainedPeer(UISearchBar Peer);

	internal sealed record CycleResult(RetainedPeer RetainedPeer, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<UISearchBar> NativePeer,
		WeakReference<Shell> Shell,
		WeakReference<ContextOnlyElementHandler> ShellHandler,
		WeakReference<TrackingImageSource>[] Sources,
		WeakReference<ContextOnlyElementHandler>[] SourceHandlers)
	{
		public static TrackedCycle Create(
			int cycle,
			UISearchBar searchBar,
			Shell shell,
			ContextOnlyElementHandler shellHandler,
			IReadOnlyList<TrackingImageSource> sources,
			IReadOnlyList<ContextOnlyElementHandler> sourceHandlers)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UISearchBar>(searchBar),
				new WeakReference<Shell>(shell),
				new WeakReference<ContextOnlyElementHandler>(shellHandler),
				sources.Select(source => new WeakReference<TrackingImageSource>(source)).ToArray(),
				sourceHandlers.Select(handler => new WeakReference<ContextOnlyElementHandler>(handler)).ToArray());
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ServiceResultsCreated,
		int ServiceResultsDisposed,
		int RetainedNativePeers,
		int NativePeersWithAssignedIcons,
		int AssignedIconStateSlots,
		long EstimatedAssignedImageBytes,
		int AliveNativePeers,
		int AliveShells,
		int AliveShellHandlers,
		int AliveSources,
		int AliveSourceHandlers)
	{
		internal static ScenarioResult From(
			string name,
			ScenarioLedger ledger,
			IReadOnlyList<RetainedPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var nativePeersWithAssignedIcons = 0;
			var assignedIconStateSlots = 0;
			long estimatedAssignedImageBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				var assignedSlots = CountAssignedIconStateSlots(retainedPeer.Peer);
				if (assignedSlots > 0)
					nativePeersWithAssignedIcons++;

				assignedIconStateSlots += assignedSlots;
				estimatedAssignedImageBytes += EstimateAssignedImageBytes(retainedPeer.Peer);
			}

			var aliveNativePeers = 0;
			var aliveShells = 0;
			var aliveShellHandlers = 0;
			var aliveSources = 0;
			var aliveSourceHandlers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.Shell.TryGetTarget(out _))
					aliveShells++;

				if (cycle.ShellHandler.TryGetTarget(out _))
					aliveShellHandlers++;

				foreach (var source in cycle.Sources)
				{
					if (source.TryGetTarget(out _))
						aliveSources++;
				}

				foreach (var handler in cycle.SourceHandlers)
				{
					if (handler.TryGetTarget(out _))
						aliveSourceHandlers++;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				ledger.ResultsCreated,
				ledger.ResultsDisposed,
				retainedPeers.Count,
				nativePeersWithAssignedIcons,
				assignedIconStateSlots,
				estimatedAssignedImageBytes,
				aliveNativePeers,
				aliveShells,
				aliveShellHandlers,
				aliveSources,
				aliveSourceHandlers);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int SourceImagePixels,
	int IconsPerSearchBar,
	int StatesPerIcon,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.ServiceResultsCreated == ReproSession.TotalIconLoads &&
		Control.ServiceResultsDisposed == ReproSession.TotalIconLoads &&
		Control.RetainedNativePeers == Cycles &&
		Control.AssignedIconStateSlots == 0 &&
		Current.ServiceResultsCreated == ReproSession.TotalIconLoads &&
		Current.ServiceResultsDisposed == 0 &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativePeersWithAssignedIcons == Cycles &&
		Current.AssignedIconStateSlots == ReproSession.TotalIconStateSlots &&
		Current.EstimatedAssignedImageBytes > Control.EstimatedAssignedImageBytes &&
		Current.AliveShells <= 1 &&
		Current.AliveShellHandlers <= 1 &&
		Current.AliveSources <= IconsPerSearchBar &&
		Current.AliveSourceHandlers <= IconsPerSearchBar;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosShellSearchBarIconImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Source image size: {SourceImagePixels} x {SourceImagePixels} pixels",
			$"Icons per search bar: {IconsPerSearchBar}",
			$"States per icon: {StatesPerIcon}",
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
			$"  native peers with assigned icons: {result.NativePeersWithAssignedIcons}/{result.TrackedCycles}",
			$"  assigned icon state slots: {result.AssignedIconStateSlots}/{ReproSession.TotalIconStateSlots}",
			$"  estimated assigned native image bytes: {result.EstimatedAssignedImageBytes:N0}",
			$"  estimated assigned native image MiB: {nativeImageMiB:N1}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveSources}/{ReproSession.TotalIconLoads}",
			$"  alive source handlers: {result.AliveSourceHandlers}/{ReproSession.TotalIconLoads}");
	}
}

internal sealed class TestShellPageRendererTracker : ShellPageRendererTracker
{
	static readonly MethodInfo SetSearchBarIconMethod =
		typeof(ShellPageRendererTracker).GetMethod("SetSearchBarIcon", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(ShellPageRendererTracker).FullName, "SetSearchBarIcon");

	public TestShellPageRendererTracker(IShellContext context)
		: base(context)
	{
	}

	public void SetSearchBarIconForTest(UISearchBar searchBar, ImageSource source, UISearchBarIcon icon)
	{
		SetSearchBarIconMethod.Invoke(this, new object[] { searchBar, source, icon });
	}
}

internal sealed class FakeShellContext : IShellContext
{
	public FakeShellContext(Shell shell)
	{
		Shell = shell;
	}

	public bool AllowFlyoutGesture => false;

	public IShellItemRenderer CurrentShellItemRenderer => null!;

	public Shell Shell { get; }

	public IShellPageRendererTracker CreatePageRendererTracker() => throw new NotSupportedException();

	public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() => throw new NotSupportedException();

	public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) => throw new NotSupportedException();

	public IShellNavBarAppearanceTracker CreateNavBarAppearanceTracker() => throw new NotSupportedException();

	public IShellTabBarAppearanceTracker CreateTabBarAppearanceTracker() => throw new NotSupportedException();

	public IShellSearchResultsRenderer CreateShellSearchResultsRenderer() => throw new NotSupportedException();
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
	public TrackingImageSource(ScenarioLedger ledger, int cycle, int iconIndex)
	{
		Ledger = ledger;
		Cycle = cycle;
		IconIndex = iconIndex;
	}

	public ScenarioLedger Ledger { get; }

	public int Cycle { get; }

	public int IconIndex { get; }

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

		var image = CreateImage(trackingSource.Cycle, trackingSource.IconIndex);
		trackingSource.Ledger.RecordCreated();

		var result = new ImageSourceServiceResult(
			image,
			dispose: trackingSource.Ledger.RecordDisposed);

		return Task.FromResult<IImageSourceServiceResult<UIImage>?>(result);
	}

	static UIImage CreateImage(int cycle, int iconIndex)
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
				(nfloat)((cycle * 37 + iconIndex * 31) % 255) / 255f,
				(nfloat)((cycle * 83 + iconIndex * 59) % 255) / 255f,
				(nfloat)((cycle * 127 + iconIndex * 97) % 255) / 255f).SetFill();
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
