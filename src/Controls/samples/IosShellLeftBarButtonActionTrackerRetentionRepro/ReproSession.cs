#nullable enable

using System.Reflection;
using System.Threading;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Graphics;
using UIKit;

namespace IosShellLeftBarButtonActionTrackerRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadBytesPerCycle = 1024 * 1024;

	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-shell-leftbarbutton-action-tracker-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext appContext)
	{
		WriteProgress("Starting iOS Shell left bar button action tracker retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: retain blank native left bar button peers without MAUI callback",
			appContext,
			retainMauiCreatedBarButton: false);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: MAUI ShellPageRendererTracker leaves native left bar button action assigned",
			appContext,
			retainMauiCreatedBarButton: true);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadBytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext appContext,
		bool retainMauiCreatedBarButton)
	{
		var retainedPeers = new List<RetainedPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 12 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, appContext, retainMauiCreatedBarButton);
			retainedPeers.Add(cycleResult.RetainedPeer);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativePeers.AddRange(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, retainedPeers, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext appContext,
		bool retainMauiCreatedBarButton)
	{
		var payload = new PayloadHolder(cycle, PayloadBytesPerCycle);
		var fontManager = new PayloadFontManager(payload);
		var cycleContext = new PayloadMauiContext(appContext, fontManager);

		var page = new ContentPage
		{
			Title = "Orders",
			Content = new Label { Text = "Orders" }
		};
		var behavior = new BackButtonBehavior
		{
			IsEnabled = true,
			IsVisible = true
		};
		Shell.SetBackButtonBehavior(page, behavior);

		var shell = CreateShell(page);
		shell.FlyoutIcon = null;

		var shellHandler = new ContextOnlyElementHandler(cycleContext);
		var pageHandler = new ContextOnlyElementHandler(cycleContext);
		shellHandler.SetVirtualView(shell);
		pageHandler.SetVirtualView(page);
		shell.Handler = shellHandler;
		page.Handler = pageHandler;

		var shellContext = new FakeShellContext(shell);
		var viewController = new UIViewController();
		var tracker = new TestShellPageRendererTracker(shellContext)
		{
			ViewController = viewController,
			Page = page,
			IsRootPage = true
		};

		tracker.SetBackButtonBehaviorForTest(behavior);
		tracker.OnFlyoutBehaviorChanged(FlyoutBehavior.Flyout);
		tracker.UpdateLeftToolbarItemsForTest();
		await DrainMainQueueAsync();

		var nativeBarButtonItem = viewController.NavigationItem.LeftBarButtonItem
			?? throw new InvalidOperationException("ShellPageRendererTracker did not create LeftBarButtonItem.");

		viewController.NavigationItem.LeftBarButtonItem = null;
		nativeBarButtonItem.Image = null;

		UIBarButtonItem retainedBarButtonItem;
		if (retainMauiCreatedBarButton)
		{
			retainedBarButtonItem = nativeBarButtonItem;
		}
		else
		{
			nativeBarButtonItem.Target = null;
			nativeBarButtonItem.Action = null;
			nativeBarButtonItem.Dispose();

			// The UIBarButtonItem event constructor keeps its managed callback even after
			// Target/Action are nulled. The control keeps a native peer alive without that
			// MAUI callback so the retained payload split is attributable to the callback.
			retainedBarButtonItem = new UIBarButtonItem();
		}

		tracker.Dispose();
		shellHandler.DisconnectHandler();
		pageHandler.DisconnectHandler();
		viewController.Dispose();
		await DrainMainQueueAsync();

		return new CycleResult(
			new RetainedPeer(retainedBarButtonItem),
			TrackedCycle.Create(
				cycle,
				retainedBarButtonItem,
				tracker,
				fontManager,
				payload,
				shell,
				page,
				shellHandler,
				pageHandler));
	}

	static Shell CreateShell(Page page)
	{
		var shell = new Shell
		{
			Title = "Operations Shell",
			FlyoutBehavior = FlyoutBehavior.Flyout
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

	internal sealed record RetainedPeer(UIBarButtonItem Peer);

	internal sealed record CycleResult(RetainedPeer RetainedPeer, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<UIBarButtonItem> NativePeer,
		WeakReference<TestShellPageRendererTracker> Tracker,
		WeakReference<PayloadFontManager> FontManager,
		WeakReference<PayloadHolder> Payload,
		WeakReference<Shell> Shell,
		WeakReference<Page> Page,
		WeakReference<ContextOnlyElementHandler> ShellHandler,
		WeakReference<ContextOnlyElementHandler> PageHandler)
	{
		public static TrackedCycle Create(
			int cycle,
			UIBarButtonItem nativeBarButtonItem,
			TestShellPageRendererTracker tracker,
			PayloadFontManager fontManager,
			PayloadHolder payload,
			Shell shell,
			Page page,
			ContextOnlyElementHandler shellHandler,
			ContextOnlyElementHandler pageHandler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UIBarButtonItem>(nativeBarButtonItem),
				new WeakReference<TestShellPageRendererTracker>(tracker),
				new WeakReference<PayloadFontManager>(fontManager),
				new WeakReference<PayloadHolder>(payload),
				new WeakReference<Shell>(shell),
				new WeakReference<Page>(page),
				new WeakReference<ContextOnlyElementHandler>(shellHandler),
				new WeakReference<ContextOnlyElementHandler>(pageHandler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativePeers,
		int NativePeersWithTarget,
		int NativePeersWithAction,
		int NativePeersWithImage,
		int AliveNativePeers,
		int AliveTrackers,
		int AliveFontManagers,
		int AlivePayloads,
		int AliveShells,
		int AlivePages,
		int AliveShellHandlers,
		int AlivePageHandlers,
		long EstimatedAlivePayloadBytes)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var nativePeersWithTarget = 0;
			var nativePeersWithAction = 0;
			var nativePeersWithImage = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				if (retainedPeer.Peer.Target is not null)
					nativePeersWithTarget++;

				if (retainedPeer.Peer.Action is not null)
					nativePeersWithAction++;

				if (retainedPeer.Peer.Image is not null)
					nativePeersWithImage++;
			}

			var aliveNativePeers = 0;
			var aliveTrackers = 0;
			var aliveFontManagers = 0;
			var alivePayloads = 0;
			var aliveShells = 0;
			var alivePages = 0;
			var aliveShellHandlers = 0;
			var alivePageHandlers = 0;
			long estimatedAlivePayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.Tracker.TryGetTarget(out _))
					aliveTrackers++;

				if (cycle.FontManager.TryGetTarget(out _))
					aliveFontManagers++;

				if (cycle.Payload.TryGetTarget(out var payload))
				{
					alivePayloads++;
					estimatedAlivePayloadBytes += payload.SizeBytes;
				}

				if (cycle.Shell.TryGetTarget(out _))
					aliveShells++;

				if (cycle.Page.TryGetTarget(out _))
					alivePages++;

				if (cycle.ShellHandler.TryGetTarget(out _))
					aliveShellHandlers++;

				if (cycle.PageHandler.TryGetTarget(out _))
					alivePageHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedPeers.Count,
				nativePeersWithTarget,
				nativePeersWithAction,
				nativePeersWithImage,
				aliveNativePeers,
				aliveTrackers,
				aliveFontManagers,
				alivePayloads,
				aliveShells,
				alivePages,
				aliveShellHandlers,
				alivePageHandlers,
				estimatedAlivePayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadBytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativePeers == Cycles &&
		Control.NativePeersWithImage == 0 &&
		Control.NativePeersWithTarget == 0 &&
		Control.NativePeersWithAction == 0 &&
		Control.AliveTrackers <= 1 &&
		Control.AlivePayloads <= 1 &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativePeersWithImage == 0 &&
		Current.NativePeersWithTarget == Cycles &&
		Current.NativePeersWithAction == Cycles &&
		Current.AliveTrackers == Cycles &&
		Current.AliveFontManagers == Cycles &&
		Current.AlivePayloads == Cycles &&
		Current.EstimatedAlivePayloadBytes >= Cycles * (long)PayloadBytesPerCycle &&
		Current.AliveShells <= 1 &&
		Current.AlivePages <= 1 &&
		Current.AliveShellHandlers == 0 &&
		Current.AlivePageHandlers <= 1;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAlivePayloadBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAlivePayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosShellLeftBarButtonActionTrackerRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload bytes per retained tracker font-manager service: {PayloadBytesPerCycle:N0}",
			$"Expected payload bytes: {Cycles * (long)PayloadBytesPerCycle:N0}",
			"Source path mirrored: ShellPageRendererTracker.UpdateLeftToolbarItems() native UIBarButtonItem action construction.",
			"Native UIBarButtonItem.Image is cleared in both runs to avoid reproving Shell left-bar image retention.",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained payload: {controlMiB:N1} MiB",
			$"Current estimated retained payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var payloadMiB = result.EstimatedAlivePayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native peers with Target: {result.NativePeersWithTarget}/{result.TrackedCycles}",
			$"  native peers with Action: {result.NativePeersWithAction}/{result.TrackedCycles}",
			$"  native peers with Image: {result.NativePeersWithImage}/{result.TrackedCycles}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive disposed ShellPageRendererTrackers: {result.AliveTrackers}/{result.TrackedCycles}",
			$"  alive font managers: {result.AliveFontManagers}/{result.TrackedCycles}",
			$"  alive payloads: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  alive shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive pages: {result.AlivePages}/{result.TrackedCycles}",
			$"  alive shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}",
			$"  alive page handlers: {result.AlivePageHandlers}/{result.TrackedCycles}",
			$"  estimated alive payload bytes: {result.EstimatedAlivePayloadBytes:N0}",
			$"  estimated alive payload MiB: {payloadMiB:N1}");
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

internal sealed class PayloadMauiContext : IMauiContext
{
	public PayloadMauiContext(IMauiContext fallback, PayloadFontManager fontManager)
	{
		Handlers = fallback.Handlers;
		Services = new PayloadServiceProvider(fallback.Services, fontManager);
	}

	public IServiceProvider Services { get; }

	public IMauiHandlersFactory Handlers { get; }
}

internal sealed class PayloadServiceProvider : IServiceProvider
{
	readonly IServiceProvider _fallback;
	readonly PayloadFontManager _fontManager;

	public PayloadServiceProvider(IServiceProvider fallback, PayloadFontManager fontManager)
	{
		_fallback = fallback;
		_fontManager = fontManager;
	}

	public object? GetService(Type serviceType)
	{
		if (serviceType == typeof(IFontManager))
			return _fontManager;

		return _fallback.GetService(serviceType);
	}
}

internal sealed class PayloadFontManager : IFontManager
{
	public PayloadFontManager(PayloadHolder payload)
	{
		Payload = payload;
	}

	public PayloadHolder Payload { get; }

	public double DefaultFontSize => 14;

	public UIFont DefaultFont => UIFont.SystemFontOfSize((nfloat)DefaultFontSize)!;

	public UIFont GetFont(Microsoft.Maui.Font font, double defaultFontSize = 0)
	{
		var size = defaultFontSize > 0 ? defaultFontSize : DefaultFontSize;
		return UIFont.SystemFontOfSize((nfloat)size)!;
	}
}

internal sealed class PayloadHolder
{
	readonly byte[] _payload;

	public PayloadHolder(int cycle, int sizeBytes)
	{
		_payload = new byte[sizeBytes];
		_payload[0] = (byte)(cycle % 251);
		_payload[^1] = (byte)((cycle * 17) % 251);
	}

	public int SizeBytes => _payload.Length;
}
