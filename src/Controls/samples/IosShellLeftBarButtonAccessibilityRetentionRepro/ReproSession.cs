#nullable enable

#pragma warning disable CS0618

using System.Reflection;
using System.Text;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Graphics;
using UIKit;

namespace IosShellLeftBarButtonAccessibilityRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 512;
	internal const int PayloadKiBPerAccessibilitySlot = 16;
	internal const int AccessibilitySlotsPerCycle = 2;

	const long PayloadBytesPerAccessibilitySlot = PayloadKiBPerAccessibilitySlot * 1024L;

	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-shell-leftbarbutton-accessibility-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS Shell left bar button accessibility retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native left bar button accessibility slots",
			context,
			clearNativeAccessibility: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: ShellPageRendererTracker leaves native left bar button accessibility assigned",
			context,
			clearNativeAccessibility: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadKiBPerAccessibilitySlot,
			AccessibilitySlotsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeAccessibility)
	{
		var retainedPeers = new List<RetainedPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 64 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, clearNativeAccessibility);
			retainedPeers.Add(cycleResult.RetainedPeer);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativePeers.AddRange(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, retainedPeers, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext context,
		bool clearNativeAccessibility)
	{
		var source = CreateImageSource(cycle);
		var page = new ContentPage
		{
			Title = "Orders",
			Content = new Label { Text = "Orders" }
		};
		var behavior = new BackButtonBehavior
		{
			IconOverride = source,
			IsEnabled = true,
			IsVisible = true
		};
		Shell.SetBackButtonBehavior(page, behavior);

		var shell = CreateShell(page);
		var shellHandler = new ContextOnlyElementHandler(context);
		var pageHandler = new ContextOnlyElementHandler(context);
		shellHandler.SetVirtualView(shell);
		pageHandler.SetVirtualView(page);
		shell.Handler = shellHandler;
		page.Handler = pageHandler;

		var nativeBarButtonItem = await CreateBarButtonItemWithCurrentPathAsync(shell, page, behavior);

		if (CountPayloadAccessibilitySlots(nativeBarButtonItem) != AccessibilitySlotsPerCycle)
			throw new InvalidOperationException("ShellPageRendererTracker did not assign the expected native accessibility payloads.");

		nativeBarButtonItem.Image = null;
		nativeBarButtonItem.Target = null;
		nativeBarButtonItem.Action = null;

		ClearManagedAccessibilityValues(source);
		behavior.IconOverride = null!;
		page.ClearValue(Shell.BackButtonBehaviorProperty);

		if (clearNativeAccessibility)
			ClearNativeAccessibility(nativeBarButtonItem);

		shellHandler.DisconnectHandler();
		pageHandler.DisconnectHandler();
		await DrainMainQueueAsync();

		return new CycleResult(
			new RetainedPeer(nativeBarButtonItem),
			TrackedCycle.Create(cycle, nativeBarButtonItem, shell, page, shellHandler, pageHandler, source));
	}

	static TrackingImageSource CreateImageSource(int cycle)
	{
		var source = new TrackingImageSource(cycle)
		{
			AutomationId = $"shell-left-{cycle:0000}"
		};

		AutomationProperties.SetName(source, CreateAccessibilityPayload(cycle, "name"));
		AutomationProperties.SetHelpText(source, CreateAccessibilityPayload(cycle, "help-text"));

		return source;
	}

	static string CreateAccessibilityPayload(int cycle, string slot)
	{
		var header = $"Cycle {cycle:0000} shell left bar {slot}. ";
		var sentence = "Generated navigation action accessibility metadata for offline workflow review, route context, and command confirmation. ";
		var targetChars = (int)(PayloadBytesPerAccessibilitySlot / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static void ClearManagedAccessibilityValues(TrackingImageSource source)
	{
		AutomationProperties.SetName(source, null);
		AutomationProperties.SetHelpText(source, null);
	}

	static void ClearNativeAccessibility(UIBarButtonItem item)
	{
		item.AccessibilityIdentifier = null;
		item.AccessibilityLabel = null;
		item.AccessibilityHint = null;
	}

	static Shell CreateShell(Page page)
	{
		var shell = new Shell
		{
			Title = "Operations Shell"
		};

		var shellContent = new ShellContent
		{
			Title = "Orders",
			Content = page
		};

		var shellSection = new ShellSection
		{
			Title = "Operations"
		};
		shellSection.Items.Add(shellContent);

		var flyoutItem = new FlyoutItem
		{
			Title = "Operations"
		};
		flyoutItem.Items.Add(shellSection);
		shell.Items.Add(flyoutItem);

		return shell;
	}

	static async Task<UIBarButtonItem> CreateBarButtonItemWithCurrentPathAsync(
		Shell shell,
		Page page,
		BackButtonBehavior behavior)
	{
		var shellContext = new FakeShellContext(shell);
		var viewController = new UIViewController();
		var tracker = new TestShellPageRendererTracker(shellContext)
		{
			ViewController = viewController,
			Page = page,
			IsRootPage = false
		};

		tracker.SetBackButtonBehaviorForTest(behavior);
		tracker.UpdateLeftToolbarItemsForTest();
		await DrainMainQueueAsync();

		var nativeBarButtonItem = viewController.NavigationItem.LeftBarButtonItem;
		if (nativeBarButtonItem is null)
			throw new InvalidOperationException("ShellPageRendererTracker did not create LeftBarButtonItem.");

		viewController.NavigationItem.LeftBarButtonItem = null;
		tracker.Dispose();
		viewController.Dispose();
		return nativeBarButtonItem;
	}

	internal static async Task DrainMainQueueAsync()
	{
		await Task.Delay(20);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.005));
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

	static int CountPayloadAccessibilitySlots(UIBarButtonItem item) =>
		GetNativeAccessibilityTexts(item).Count(text => EstimateTextBytes(text) >= PayloadBytesPerAccessibilitySlot * 0.95);

	static long EstimateNativeAccessibilityBytes(UIBarButtonItem item)
	{
		long total = 0;
		foreach (var text in GetNativeAccessibilityTexts(item))
		{
			var bytes = EstimateTextBytes(text);
			if (bytes >= PayloadBytesPerAccessibilitySlot * 0.95)
				total += Math.Min(bytes, PayloadBytesPerAccessibilitySlot);
		}

		return total;
	}

	static IEnumerable<string?> GetNativeAccessibilityTexts(UIBarButtonItem item)
	{
		yield return item.AccessibilityIdentifier;
		yield return item.AccessibilityLabel;
		yield return item.AccessibilityHint;
	}

	static long EstimateTextBytes(string? text) =>
		string.IsNullOrEmpty(text) ? 0 : text.Length * 2L;

	internal sealed record RetainedPeer(UIBarButtonItem Peer);

	internal sealed record CycleResult(RetainedPeer RetainedPeer, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<UIBarButtonItem> NativePeer,
		WeakReference<Shell> Shell,
		WeakReference<Page> Page,
		WeakReference<ContextOnlyElementHandler> ShellHandler,
		WeakReference<ContextOnlyElementHandler> PageHandler,
		WeakReference<TrackingImageSource> Source)
	{
		public static TrackedCycle Create(
			int cycle,
			UIBarButtonItem nativeBarButtonItem,
			Shell shell,
			Page page,
			ContextOnlyElementHandler shellHandler,
			ContextOnlyElementHandler pageHandler,
			TrackingImageSource source)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UIBarButtonItem>(nativeBarButtonItem),
				new WeakReference<Shell>(shell),
				new WeakReference<Page>(page),
				new WeakReference<ContextOnlyElementHandler>(shellHandler),
				new WeakReference<ContextOnlyElementHandler>(pageHandler),
				new WeakReference<TrackingImageSource>(source));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativePeers,
		int AssignedPayloadSizedAccessibilitySlots,
		long EstimatedNativeAccessibilityBytes,
		int AliveNativePeers,
		int AliveShells,
		int AlivePages,
		int AliveShellHandlers,
		int AlivePageHandlers,
		int AliveSources)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var assignedPayloadSizedAccessibilitySlots = 0;
			long estimatedNativeAccessibilityBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				assignedPayloadSizedAccessibilitySlots += CountPayloadAccessibilitySlots(retainedPeer.Peer);
				estimatedNativeAccessibilityBytes += EstimateNativeAccessibilityBytes(retainedPeer.Peer);
			}

			var aliveNativePeers = 0;
			var aliveShells = 0;
			var alivePages = 0;
			var aliveShellHandlers = 0;
			var alivePageHandlers = 0;
			var aliveSources = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.Shell.TryGetTarget(out _))
					aliveShells++;

				if (cycle.Page.TryGetTarget(out _))
					alivePages++;

				if (cycle.ShellHandler.TryGetTarget(out _))
					aliveShellHandlers++;

				if (cycle.PageHandler.TryGetTarget(out _))
					alivePageHandlers++;

				if (cycle.Source.TryGetTarget(out _))
					aliveSources++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedPeers.Count,
				assignedPayloadSizedAccessibilitySlots,
				estimatedNativeAccessibilityBytes,
				aliveNativePeers,
				aliveShells,
				alivePages,
				aliveShellHandlers,
				alivePageHandlers,
				aliveSources);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerAccessibilitySlot,
	int AccessibilitySlotsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativePeers == Cycles &&
		Control.AssignedPayloadSizedAccessibilitySlots == 0 &&
		Current.RetainedNativePeers == Cycles &&
		Current.AssignedPayloadSizedAccessibilitySlots == Cycles * AccessibilitySlotsPerCycle &&
		Current.EstimatedNativeAccessibilityBytes >= Cycles * AccessibilitySlotsPerCycle * PayloadKiBPerAccessibilitySlot * 1024L * 0.95 &&
		Current.AliveShellHandlers == 0 &&
		Current.AlivePageHandlers <= 1;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedNativeAccessibilityBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedNativeAccessibilityBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosShellLeftBarButtonAccessibilityRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per native accessibility slot: {PayloadKiBPerAccessibilitySlot} KiB",
			$"Payload-sized native accessibility label/hint slots per cycle: {AccessibilitySlotsPerCycle}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native accessibility payload: {controlMiB:N1} MiB",
			$"Current estimated retained native accessibility payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeAccessibilityMiB = result.EstimatedNativeAccessibilityBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  assigned payload-sized accessibility slots: {result.AssignedPayloadSizedAccessibilitySlots}/{result.TrackedCycles * ReproSession.AccessibilitySlotsPerCycle}",
			$"  estimated retained native accessibility bytes: {result.EstimatedNativeAccessibilityBytes:N0}",
			$"  estimated retained native accessibility MiB: {nativeAccessibilityMiB:N1}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive pages: {result.AlivePages}/{result.TrackedCycles}",
			$"  alive shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}",
			$"  alive page handlers: {result.AlivePageHandlers}/{result.TrackedCycles}",
			$"  alive image sources: {result.AliveSources}/{result.TrackedCycles}");
	}
}

internal sealed class TestShellPageRendererTracker : ShellPageRendererTracker
{
	static readonly MethodInfo SetBackButtonBehaviorMethod =
		typeof(ShellPageRendererTracker).GetMethod("SetBackButtonBehavior", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(ShellPageRendererTracker).FullName, "SetBackButtonBehavior");

	static readonly MethodInfo UpdateLeftToolbarItemsMethod =
		typeof(ShellPageRendererTracker).GetMethod("UpdateLeftToolbarItems", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(ShellPageRendererTracker).FullName, "UpdateLeftToolbarItems");

	public TestShellPageRendererTracker(IShellContext context)
		: base(context)
	{
	}

	public void SetBackButtonBehaviorForTest(BackButtonBehavior behavior)
	{
		SetBackButtonBehaviorMethod.Invoke(this, new object[] { behavior });
	}

	public void UpdateLeftToolbarItemsForTest()
	{
		UpdateLeftToolbarItemsMethod.Invoke(this, Array.Empty<object>());
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

internal sealed class TrackingImageSource : ImageSource
{
	public TrackingImageSource(int cycle)
	{
		Cycle = cycle;
	}

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
		var result = new ImageSourceServiceResult(image);
		return Task.FromResult<IImageSourceServiceResult<UIImage>?>(result);
	}

	static UIImage CreateImage(int cycle)
	{
		var format = new UIGraphicsImageRendererFormat
		{
			Opaque = true,
			Scale = 1
		};
		var renderer = new UIGraphicsImageRenderer(new CGSize(8, 8), format);

		return renderer.CreateImage(context =>
		{
			UIColor.FromRGB(
				(nfloat)((cycle * 37) % 255) / 255f,
				(nfloat)((cycle * 83) % 255) / 255f,
				(nfloat)((cycle * 127) % 255) / 255f).SetFill();
			context.FillRect(new CGRect(0, 0, 8, 8));
		});
	}
}
