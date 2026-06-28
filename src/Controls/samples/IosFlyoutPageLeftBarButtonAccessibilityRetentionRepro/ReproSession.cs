#nullable enable

#pragma warning disable CS0618

using System.Reflection;
using System.Text;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Graphics;
using UIKit;

namespace IosFlyoutPageLeftBarButtonAccessibilityRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 512;
	internal const int PayloadKiBPerAccessibilitySlot = 16;
	internal const int AccessibilitySlotsPerCycle = 2;

	const long PayloadBytesPerAccessibilitySlot = PayloadKiBPerAccessibilitySlot * 1024L;

	static readonly MethodInfo SetFlyoutLeftBarButtonMethod =
		typeof(NavigationRenderer).GetMethod("SetFlyoutLeftBarButton", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(NavigationRenderer).FullName, "SetFlyoutLeftBarButton");

	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-flyoutpage-leftbarbutton-accessibility-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS FlyoutPage left bar button accessibility retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native FlyoutPage left bar button accessibility slots",
			context,
			clearNativeAccessibility: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: NavigationRenderer leaves FlyoutPage left bar button accessibility assigned",
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
		var flyoutPage = CreateFlyoutPage(cycle);
		var flyoutHandler = AttachContext(flyoutPage, context);
		var flyoutContentHandler = AttachContext(flyoutPage.Flyout, context);
		var detailHandler = AttachContext(flyoutPage.Detail, context);

		var nativeBarButtonItem = await CreateBarButtonItemWithCurrentPathAsync(flyoutPage);

		if (CountPayloadAccessibilitySlots(nativeBarButtonItem) != AccessibilitySlotsPerCycle)
			throw new InvalidOperationException("NavigationRenderer did not assign the expected native accessibility payloads.");

		// Keep this proof focused on accessibility text, not the adjacent image/title/action leaks.
		nativeBarButtonItem.Image = null;
		nativeBarButtonItem.Title = string.Empty;
		nativeBarButtonItem.Target = null;
		nativeBarButtonItem.Action = null;
		nativeBarButtonItem.AccessibilityIdentifier = null;

		ClearManagedAccessibilityValues(flyoutPage);

		if (clearNativeAccessibility)
			ClearNativeAccessibility(nativeBarButtonItem);

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
				detailHandler));
	}

	static FlyoutPage CreateFlyoutPage(int cycle)
	{
		var flyoutPage = new FlyoutPage
		{
			Title = $"Operations {cycle:000}",
			AutomationId = $"flyout-left-{cycle:0000}",
			FlyoutLayoutBehavior = FlyoutLayoutBehavior.Popover,
			Flyout = new ContentPage
			{
				Title = $"Menu {cycle:000}",
				Content = new Label { Text = "Menu" }
			},
			Detail = new ContentPage
			{
				Title = $"Detail {cycle:000}",
				Content = new Label { Text = "Detail" }
			}
		};

		AutomationProperties.SetName(flyoutPage, CreateAccessibilityPayload(cycle, "name"));
		AutomationProperties.SetHelpText(flyoutPage, CreateAccessibilityPayload(cycle, "help-text"));

		return flyoutPage;
	}

	static string CreateAccessibilityPayload(int cycle, string slot)
	{
		var header = $"Cycle {cycle:0000} legacy FlyoutPage left bar {slot}. ";
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

	static void ClearManagedAccessibilityValues(FlyoutPage flyoutPage)
	{
		AutomationProperties.SetName(flyoutPage, null);
		AutomationProperties.SetHelpText(flyoutPage, null);
	}

	static void ClearNativeAccessibility(UIBarButtonItem item)
	{
		item.AccessibilityIdentifier = null;
		item.AccessibilityLabel = null;
		item.AccessibilityHint = null;
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
		viewController.Dispose();

		return nativeBarButtonItem;
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
		WeakReference<FlyoutPage> FlyoutPage,
		WeakReference<Page> Flyout,
		WeakReference<Page> Detail,
		WeakReference<ContextOnlyElementHandler> FlyoutPageHandler,
		WeakReference<ContextOnlyElementHandler> FlyoutHandler,
		WeakReference<ContextOnlyElementHandler> DetailHandler)
	{
		public static TrackedCycle Create(
			int cycle,
			UIBarButtonItem nativeBarButtonItem,
			FlyoutPage flyoutPage,
			Page flyout,
			Page detail,
			ContextOnlyElementHandler flyoutPageHandler,
			ContextOnlyElementHandler flyoutHandler,
			ContextOnlyElementHandler detailHandler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UIBarButtonItem>(nativeBarButtonItem),
				new WeakReference<FlyoutPage>(flyoutPage),
				new WeakReference<Page>(flyout),
				new WeakReference<Page>(detail),
				new WeakReference<ContextOnlyElementHandler>(flyoutPageHandler),
				new WeakReference<ContextOnlyElementHandler>(flyoutHandler),
				new WeakReference<ContextOnlyElementHandler>(detailHandler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativePeers,
		int AssignedPayloadSizedAccessibilitySlots,
		long EstimatedNativeAccessibilityBytes,
		int AliveNativePeers,
		int AliveFlyoutPages,
		int AliveFlyouts,
		int AliveDetails,
		int AliveFlyoutPageHandlers,
		int AliveFlyoutHandlers,
		int AliveDetailHandlers)
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
			var aliveFlyoutPages = 0;
			var aliveFlyouts = 0;
			var aliveDetails = 0;
			var aliveFlyoutPageHandlers = 0;
			var aliveFlyoutHandlers = 0;
			var aliveDetailHandlers = 0;

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
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedPeers.Count,
				assignedPayloadSizedAccessibilitySlots,
				estimatedNativeAccessibilityBytes,
				aliveNativePeers,
				aliveFlyoutPages,
				aliveFlyouts,
				aliveDetails,
				aliveFlyoutPageHandlers,
				aliveFlyoutHandlers,
				aliveDetailHandlers);
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
		Control.AliveFlyoutPageHandlers <= 1 &&
		Control.AliveFlyoutHandlers <= 1 &&
		Control.AliveDetailHandlers <= 1 &&
		Current.RetainedNativePeers == Cycles &&
		Current.AssignedPayloadSizedAccessibilitySlots == Cycles * AccessibilitySlotsPerCycle &&
		Current.EstimatedNativeAccessibilityBytes >= Cycles * AccessibilitySlotsPerCycle * PayloadKiBPerAccessibilitySlot * 1024L * 0.95 &&
		Current.AliveFlyoutPageHandlers <= 1 &&
		Current.AliveFlyoutHandlers <= 1 &&
		Current.AliveDetailHandlers <= 1;

	public string ToText()
	{
		var controlMiB = Control.EstimatedNativeAccessibilityBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedNativeAccessibilityBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosFlyoutPageLeftBarButtonAccessibilityRetentionRepro",
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
			$"  alive FlyoutPages: {result.AliveFlyoutPages}/{result.TrackedCycles}",
			$"  alive flyout pages: {result.AliveFlyouts}/{result.TrackedCycles}",
			$"  alive detail pages: {result.AliveDetails}/{result.TrackedCycles}",
			$"  alive FlyoutPage handlers: {result.AliveFlyoutPageHandlers}/{result.TrackedCycles}",
			$"  alive flyout handlers: {result.AliveFlyoutHandlers}/{result.TrackedCycles}",
			$"  alive detail handlers: {result.AliveDetailHandlers}/{result.TrackedCycles}");
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
