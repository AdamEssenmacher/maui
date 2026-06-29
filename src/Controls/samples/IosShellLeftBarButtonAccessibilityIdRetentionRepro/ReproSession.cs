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

namespace IosShellLeftBarButtonAccessibilityIdRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 1024;
	internal const int PayloadKiBPerIdentifierSlot = 16;
	internal const int IdentifierSlotsPerCycle = 1;

	const long PayloadBytesPerIdentifierSlot = PayloadKiBPerIdentifierSlot * 1024L;

	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-shell-leftbarbutton-accessibilityid-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS Shell left bar button accessibility identifier retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native left bar button accessibility identifier",
			context,
			clearNativeIdentifier: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: ShellPageRendererTracker leaves native left bar button accessibility identifier assigned",
			context,
			clearNativeIdentifier: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadKiBPerIdentifierSlot,
			IdentifierSlotsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeIdentifier)
	{
		var retainedPeers = new List<RetainedPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 64 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, clearNativeIdentifier);
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
		bool clearNativeIdentifier)
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

		if (CountPayloadIdentifierSlots(nativeBarButtonItem) != IdentifierSlotsPerCycle)
			throw new InvalidOperationException("ShellPageRendererTracker did not assign the expected native accessibility identifier payload.");

		// Keep this proof focused on the accessibility identifier, not adjacent image/action/label/hint retention.
		nativeBarButtonItem.Image = null;
		nativeBarButtonItem.Target = null;
		nativeBarButtonItem.Action = null;
		nativeBarButtonItem.AccessibilityLabel = null;
		nativeBarButtonItem.AccessibilityHint = null;

		source.ClearValue(Element.AutomationIdProperty);
		behavior.IconOverride = null!;
		page.ClearValue(Shell.BackButtonBehaviorProperty);

		if (clearNativeIdentifier)
			ClearNativeIdentifier(nativeBarButtonItem);

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
			AutomationId = CreateIdentifierPayload(cycle)
		};

		return source;
	}

	static string CreateIdentifierPayload(int cycle)
	{
		var header = $"cycle-{cycle:0000}-shell-leftbar-icon-route-";
		var sentence = "generated-navigation-action-automation-id-workflow-context-command-confirmation-";
		var targetChars = (int)(PayloadBytesPerIdentifierSlot / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static void ClearNativeIdentifier(UIBarButtonItem item)
	{
		item.AccessibilityIdentifier = null;
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

	static int CountPayloadIdentifierSlots(UIBarButtonItem item) =>
		EstimateTextBytes(item.AccessibilityIdentifier) >= PayloadBytesPerIdentifierSlot * 0.95 ? 1 : 0;

	static long EstimateNativeIdentifierBytes(UIBarButtonItem item)
	{
		var bytes = EstimateTextBytes(item.AccessibilityIdentifier);
		return bytes >= PayloadBytesPerIdentifierSlot * 0.95 ? Math.Min(bytes, PayloadBytesPerIdentifierSlot) : 0;
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
		int AssignedPayloadSizedIdentifierSlots,
		long EstimatedNativeIdentifierBytes,
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
			var assignedPayloadSizedIdentifierSlots = 0;
			long estimatedNativeIdentifierBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				assignedPayloadSizedIdentifierSlots += CountPayloadIdentifierSlots(retainedPeer.Peer);
				estimatedNativeIdentifierBytes += EstimateNativeIdentifierBytes(retainedPeer.Peer);
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
				assignedPayloadSizedIdentifierSlots,
				estimatedNativeIdentifierBytes,
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
	int PayloadKiBPerIdentifierSlot,
	int IdentifierSlotsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
public bool LeakProved =>
		Control.RetainedNativePeers == Cycles &&
		Control.AssignedPayloadSizedIdentifierSlots == 0 &&
		Current.RetainedNativePeers == Cycles &&
		Current.AssignedPayloadSizedIdentifierSlots == Cycles * IdentifierSlotsPerCycle &&
		Current.EstimatedNativeIdentifierBytes >= Cycles * IdentifierSlotsPerCycle * PayloadKiBPerIdentifierSlot * 1024L * 0.95 &&
		Current.AliveShellHandlers == 0 &&
		Current.AlivePageHandlers <= 1;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedNativeIdentifierBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedNativeIdentifierBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosShellLeftBarButtonAccessibilityIdRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per native accessibility identifier slot: {PayloadKiBPerIdentifierSlot} KiB",
			$"Payload-sized native accessibility identifier slots per cycle: {IdentifierSlotsPerCycle}",
			"Note: the Shell image-source path keeps image sources alive in both runs; this repro isolates the extra native AccessibilityIdentifier payload.",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native accessibility identifier payload: {controlMiB:N1} MiB",
			$"Current estimated retained native accessibility identifier payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeIdentifierMiB = result.EstimatedNativeIdentifierBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  assigned payload-sized accessibility identifier slots: {result.AssignedPayloadSizedIdentifierSlots}/{result.TrackedCycles * ReproSession.IdentifierSlotsPerCycle}",
			$"  estimated retained native accessibility identifier bytes: {result.EstimatedNativeIdentifierBytes:N0}",
			$"  estimated retained native accessibility identifier MiB: {nativeIdentifierMiB:N1}",
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
