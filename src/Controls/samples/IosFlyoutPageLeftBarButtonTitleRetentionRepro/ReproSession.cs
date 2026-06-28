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

namespace IosFlyoutPageLeftBarButtonTitleRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerTitle = 256;
	internal const int PayloadBytes = 1024 * 1024;

	const long PayloadBytesPerTitle = PayloadKiBPerTitle * 1024L;

	static readonly MethodInfo SetFlyoutLeftBarButtonMethod =
		typeof(NavigationRenderer).GetMethod("SetFlyoutLeftBarButton", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(NavigationRenderer).FullName, "SetFlyoutLeftBarButton");

	static readonly List<RetainedPeer> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-flyoutpage-leftbarbutton-title-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS FlyoutPage left bar button title retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native FlyoutPage left bar button title and avoid page-capturing action",
			context,
			clearNativeTitle: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: MAUI NavigationRenderer leaves FlyoutPage left bar button title assigned",
			context,
			clearNativeTitle: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadKiBPerTitle,
			PayloadBytes,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeTitle)
	{
		var retainedPeers = new List<RetainedPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, clearNativeTitle);
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
		bool clearNativeTitle)
	{
		var title = CreateLargeTitle(cycle);
		var payload = new PayloadViewModel(cycle);
		var flyoutPage = CreateFlyoutPage(title, payload, cycle);
		var flyoutHandler = AttachContext(flyoutPage, context);
		var flyoutContentHandler = AttachContext(flyoutPage.Flyout, context);
		var detailHandler = AttachContext(flyoutPage.Detail, context);

		var nativeBarButtonItem = clearNativeTitle
			? await CreateBarButtonItemWithControlPathAsync(title)
			: await CreateBarButtonItemWithCurrentPathAsync(flyoutPage);

		if (EstimateTitleBytes(nativeBarButtonItem.Title) < PayloadBytesPerTitle * 0.95)
			throw new InvalidOperationException("NavigationRenderer did not assign the payload-sized native title.");

		// Remove the managed title after native assignment so retained page graphs do not explain the title payload.
		flyoutPage.Flyout.Title = string.Empty;

		if (clearNativeTitle)
			nativeBarButtonItem.Title = string.Empty;

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
				payload));
	}

	static FlyoutPage CreateFlyoutPage(string flyoutTitle, PayloadViewModel payload, int cycle)
	{
		return new FlyoutPage
		{
			Title = $"Operations {cycle:000}",
			BindingContext = payload,
			FlyoutLayoutBehavior = FlyoutLayoutBehavior.Popover,
			Flyout = new ContentPage
			{
				Title = flyoutTitle,
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

	static async Task<UIBarButtonItem> CreateBarButtonItemWithControlPathAsync(string title)
	{
		var nativeBarButtonItem = new UIBarButtonItem(title, UIBarButtonItemStyle.Plain, static (_, _) => { });
		nativeBarButtonItem.Target = null;
		nativeBarButtonItem.Action = null;
		await DrainMainQueueAsync();
		return nativeBarButtonItem;
	}

	static string CreateLargeTitle(int cycle)
	{
		var header = $"Flyout workflow title {cycle:000}. ";
		var sentence = "Generated workspace, offline case list, routed operation group, and review queue. ";
		var targetChars = (int)(PayloadBytesPerTitle / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
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

	static int CountAssignedPayloadTitles(UIBarButtonItem item)
	{
		return EstimateTitleBytes(item.Title) >= PayloadBytesPerTitle * 0.95 ? 1 : 0;
	}

	static long EstimateAssignedTitleBytes(UIBarButtonItem item)
	{
		return Math.Min(EstimateTitleBytes(item.Title), PayloadBytesPerTitle);
	}

	static long EstimateTitleBytes(string? title)
	{
		return string.IsNullOrEmpty(title) ? 0 : title.Length * 2L;
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
				new WeakReference<PayloadViewModel>(payload),
				new WeakReference<byte[]>(payload.Payload));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativePeers,
		int AssignedPayloadTitles,
		long EstimatedAssignedTitleBytes,
		int AliveNativePeers,
		int AliveFlyoutPages,
		int AliveFlyouts,
		int AliveDetails,
		int AliveFlyoutPageHandlers,
		int AliveFlyoutHandlers,
		int AliveDetailHandlers,
		int AlivePayloads,
		int AlivePayloadByteArrays,
		long AlivePayloadBytes)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var assignedPayloadTitles = 0;
			long estimatedAssignedTitleBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				assignedPayloadTitles += CountAssignedPayloadTitles(retainedPeer.Peer);
				estimatedAssignedTitleBytes += EstimateAssignedTitleBytes(retainedPeer.Peer);
			}

			var aliveNativePeers = 0;
			var aliveFlyoutPages = 0;
			var aliveFlyouts = 0;
			var aliveDetails = 0;
			var aliveFlyoutPageHandlers = 0;
			var aliveFlyoutHandlers = 0;
			var aliveDetailHandlers = 0;
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
				retainedPeers.Count,
				assignedPayloadTitles,
				estimatedAssignedTitleBytes,
				aliveNativePeers,
				aliveFlyoutPages,
				aliveFlyouts,
				aliveDetails,
				aliveFlyoutPageHandlers,
				aliveFlyoutHandlers,
				aliveDetailHandlers,
				alivePayloads,
				alivePayloadByteArrays,
				alivePayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerTitle,
	int PayloadBytes,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativePeers == Cycles &&
		Control.AssignedPayloadTitles == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.RetainedNativePeers == Cycles &&
		Current.AssignedPayloadTitles == Cycles &&
		Current.EstimatedAssignedTitleBytes >= Cycles * PayloadKiBPerTitle * 1024L * 0.95 &&
		Current.AliveFlyoutPages == Cycles &&
		Current.AliveFlyouts == Cycles &&
		Current.AliveDetails == Cycles &&
		Current.AliveFlyoutPageHandlers <= 1 &&
		Current.AliveFlyoutHandlers <= 1 &&
		Current.AliveDetailHandlers <= 1 &&
		Current.AlivePayloads == Cycles &&
		Current.AlivePayloadByteArrays == Cycles;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var payloadMiB = Current.AlivePayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosFlyoutPageLeftBarButtonTitleRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per native title: {PayloadKiBPerTitle} KiB",
			$"Managed payload per cycle: {PayloadBytes:N0} bytes",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native title payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native title payload: {retainedMiB:N1} MiB",
			$"Current retained managed payload: {payloadMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeTitleMiB = result.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var payloadMiB = result.AlivePayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  assigned payload-sized titles: {result.AssignedPayloadTitles}/{result.TrackedCycles}",
			$"  estimated assigned native title bytes: {result.EstimatedAssignedTitleBytes:N0}",
			$"  estimated assigned native title MiB: {nativeTitleMiB:N1}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive FlyoutPages: {result.AliveFlyoutPages}/{result.TrackedCycles}",
			$"  alive flyout pages: {result.AliveFlyouts}/{result.TrackedCycles}",
			$"  alive detail pages: {result.AliveDetails}/{result.TrackedCycles}",
			$"  alive FlyoutPage handlers: {result.AliveFlyoutPageHandlers}/{result.TrackedCycles}",
			$"  alive flyout handlers: {result.AliveFlyoutHandlers}/{result.TrackedCycles}",
			$"  alive detail handlers: {result.AliveDetailHandlers}/{result.TrackedCycles}",
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
