#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using ObjCRuntime;
using UIKit;

namespace IosTabbedRendererTabBarItemAccessibilityRetentionRepro;

#pragma warning disable CS0618

internal static class ReproSession
{
	internal const int Cycles = 1024;
	internal const int PayloadKiBPerAccessibilityIdentifier = 16;

	const long PayloadBytesPerAccessibilityIdentifier = PayloadKiBPerAccessibilityIdentifier * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedTabPeer>> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-tabbedrenderer-tabbaritem-accessibility-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS TabbedRenderer tab bar accessibility retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native UITabBarItem accessibility identifier before teardown",
			context,
			clearNativeAccessibilityIdentifier: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: TabbedRenderer leaves native UITabBarItem accessibility identifier assigned",
			context,
			clearNativeAccessibilityIdentifier: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadKiBPerAccessibilityIdentifier,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeAccessibilityIdentifier)
	{
		var retainedPeers = new List<RetainedTabPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 128 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, clearNativeAccessibilityIdentifier);
			retainedPeers.Add(cycleResult.RetainedPeer);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativePeers.Add(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, retainedPeers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext context,
		bool clearNativeAccessibilityIdentifier)
	{
		var childPage = new ContentPage
		{
			Title = $"Orders {cycle:0000}",
			AutomationId = CreateOperationalAutomationId(cycle)
		};

		var tabbedPage = new TabbedPage
		{
			Title = $"Workspace {cycle:0000}"
		};
		tabbedPage.Children.Add(childPage);

		var renderer = new TabbedRenderer();
		((IElementHandler)renderer).SetMauiContext(context);
		((IElementHandler)renderer).SetVirtualView(tabbedPage);

		await DrainMainQueueAsync();

		if (childPage.Handler is not IPlatformViewHandler childHandler)
			throw new InvalidOperationException("TabbedRenderer did not create a child page handler.");

		var nativeItem = childHandler.ViewController?.TabBarItem;
		if (nativeItem is null)
			throw new InvalidOperationException("TabbedRenderer did not assign a UITabBarItem.");

		if (!NativeAccessibilityIdentifierHasPayload(nativeItem))
			throw new InvalidOperationException("TabbedRenderer did not assign the expected native accessibility identifier payload.");

		var retainedPeer = RetainNativePeer(nativeItem);

		// C221 already proves UITabBarItem.Title retention; clear it in both runs so this proof isolates AccessibilityIdentifier.
		nativeItem.Title = null;

		if (clearNativeAccessibilityIdentifier)
			ClearNativeAccessibilityIdentifier(nativeItem);

		((IElementHandler)renderer).DisconnectHandler();
		childHandler.DisconnectHandler();
		tabbedPage.Children.Clear();
		childPage.Handler = null;
		tabbedPage.Handler = null;
		nativeItem.Dispose();
		await DrainMainQueueAsync();

		return new CycleResult(
			retainedPeer,
			TrackedCycle.Create(cycle, tabbedPage, childPage, childHandler));
	}

	static string CreateOperationalAutomationId(int cycle)
	{
		var header = $"cycle-{cycle:0000}-tabbed-page-accessibility-identifier-";
		var sentence = "generated-diagnostic-tab-route-offline-workspace-regional-queue-policy-exception-fulfillment-review-";
		var targetChars = (int)(PayloadBytesPerAccessibilityIdentifier / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static bool NativeAccessibilityIdentifierHasPayload(UITabBarItem item) =>
		EstimateAccessibilityIdentifierBytes(item.AccessibilityIdentifier) >= PayloadBytesPerAccessibilityIdentifier * 0.95;

	static void ClearNativeAccessibilityIdentifier(UITabBarItem item)
	{
		item.AccessibilityIdentifier = null;
	}

	static RetainedTabPeer RetainNativePeer(UITabBarItem item)
	{
		var handle = item.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UITabBarItem peer with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedTabPeer(retained);
	}

	static NativeAccessibilitySnapshot GetNativeAccessibilitySnapshot(RetainedTabPeer retainedPeer)
	{
		var peer = retainedPeer.TryGetPeer();
		if (peer is null)
			return new NativeAccessibilitySnapshot(Alive: false, EstimatedAccessibilityIdentifierBytes: 0);

		return new NativeAccessibilitySnapshot(
			Alive: true,
			EstimateAccessibilityIdentifierBytes(peer.AccessibilityIdentifier));
	}

	static long EstimateAccessibilityIdentifierBytes(string? identifier) =>
		string.IsNullOrEmpty(identifier) ? 0 : identifier.Length * 2L;

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
			NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(50);
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

	internal sealed record NativeAccessibilitySnapshot(bool Alive, long EstimatedAccessibilityIdentifierBytes);

	internal sealed record RetainedTabPeer(IntPtr Handle)
	{
		public UITabBarItem? TryGetPeer()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UITabBarItem>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record CycleResult(
		RetainedTabPeer RetainedPeer,
		TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<TabbedPage> TabbedPage,
		WeakReference<ContentPage> ChildPage,
		WeakReference<IPlatformViewHandler> ChildHandler)
	{
		public static TrackedCycle Create(
			int cycle,
			TabbedPage tabbedPage,
			ContentPage childPage,
			IPlatformViewHandler childHandler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<TabbedPage>(tabbedPage),
				new WeakReference<ContentPage>(childPage),
				new WeakReference<IPlatformViewHandler>(childHandler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativePeers,
		int NativePeersWithPayloadAccessibilityIdentifier,
		long EstimatedNativeAccessibilityIdentifierBytes,
		int AliveTabbedPages,
		int AliveChildPages,
		int AliveChildHandlers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedTabPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativePeers = 0;
			var nativePeersWithPayloadAccessibilityIdentifier = 0;
			long estimatedNativeAccessibilityIdentifierBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				var snapshot = GetNativeAccessibilitySnapshot(retainedPeer);
				if (!snapshot.Alive)
					continue;

				retainedNativePeers++;
				if (snapshot.EstimatedAccessibilityIdentifierBytes >= PayloadBytesPerAccessibilityIdentifier * 0.95)
				{
					nativePeersWithPayloadAccessibilityIdentifier++;
					estimatedNativeAccessibilityIdentifierBytes += Math.Min(
						snapshot.EstimatedAccessibilityIdentifierBytes,
						PayloadBytesPerAccessibilityIdentifier);
				}
			}

			var aliveTabbedPages = 0;
			var aliveChildPages = 0;
			var aliveChildHandlers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.TabbedPage.TryGetTarget(out _))
					aliveTabbedPages++;

				if (cycle.ChildPage.TryGetTarget(out _))
					aliveChildPages++;

				if (cycle.ChildHandler.TryGetTarget(out _))
					aliveChildHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativePeers,
				nativePeersWithPayloadAccessibilityIdentifier,
				estimatedNativeAccessibilityIdentifierBytes,
				aliveTabbedPages,
				aliveChildPages,
				aliveChildHandlers);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerAccessibilityIdentifier,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativePeers == Cycles &&
		Control.NativePeersWithPayloadAccessibilityIdentifier == 0 &&
		Control.AliveTabbedPages <= 1 &&
		Control.AliveChildPages <= 1 &&
		Control.AliveChildHandlers <= 1 &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativePeersWithPayloadAccessibilityIdentifier == Cycles &&
		Current.EstimatedNativeAccessibilityIdentifierBytes >= Cycles * PayloadKiBPerAccessibilityIdentifier * 1024L * 0.95 &&
		Current.AliveTabbedPages <= 1 &&
		Current.AliveChildPages <= 1 &&
		Current.AliveChildHandlers <= 1;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedNativeAccessibilityIdentifierBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedNativeAccessibilityIdentifierBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosTabbedRendererTabBarItemAccessibilityRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per native accessibility identifier: {PayloadKiBPerAccessibilityIdentifier} KiB",
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
		var retainedMiB = result.EstimatedNativeAccessibilityIdentifierBytes / 1024d / 1024d;
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native peers with payload-sized accessibility identifiers: {result.NativePeersWithPayloadAccessibilityIdentifier}/{result.TrackedCycles}",
			$"  estimated retained native accessibility identifier bytes: {result.EstimatedNativeAccessibilityIdentifierBytes:N0}",
			$"  estimated retained native accessibility identifier MiB: {retainedMiB:N1}",
			$"  alive TabbedPages: {result.AliveTabbedPages}/{result.TrackedCycles}",
			$"  alive child pages: {result.AliveChildPages}/{result.TrackedCycles}",
			$"  alive child handlers: {result.AliveChildHandlers}/{result.TrackedCycles}");
	}
}
