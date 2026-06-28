#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform.Compatibility;
using ObjCRuntime;
using UIKit;

namespace IosTabBarItemTitleRetentionRepro;

#pragma warning disable CS0618

internal static class ReproSession
{
	internal const int Cycles = 1024;
	internal const int PayloadKiBPerTitle = 2;
	internal const int TitleSlotsPerCycle = 2;

	const long PayloadBytesPerTitle = PayloadKiBPerTitle * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedTabPeer>> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-tabbaritem-title-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS TabBarItem title retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear native UITabBarItem title slots before teardown",
			context,
			clearNativeTitleBeforeTeardown: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: tab renderer cleanup leaves native UITabBarItem titles assigned",
			context,
			clearNativeTitleBeforeTeardown: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadKiBPerTitle,
			TitleSlotsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeTitleBeforeTeardown)
	{
		var retainedPeers = new List<RetainedTabPeer>(Cycles * TitleSlotsPerCycle);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 128 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, clearNativeTitleBeforeTeardown);
			retainedPeers.Add(cycleResult.TabbedPeer);
			retainedPeers.Add(cycleResult.ShellPeer);
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
		bool clearNativeTitleBeforeTeardown)
	{
		var tabbed = await CreateTabbedPeerAsync(cycle, context, clearNativeTitleBeforeTeardown);
		var shell = await CreateShellPeerAsync(cycle, context, clearNativeTitleBeforeTeardown);

		return new CycleResult(
			tabbed.RetainedPeer,
			shell.RetainedPeer,
			TrackedCycle.Create(
				cycle,
				tabbed.TabbedPage,
				tabbed.ChildPage,
				tabbed.ChildHandler,
				shell.Shell,
				shell.Section,
				shell.SectionHandler));
	}

	static async Task<TabbedCycleResult> CreateTabbedPeerAsync(
		int cycle,
		IMauiContext context,
		bool clearNativeTitleBeforeTeardown)
	{
		var childPage = new ContentPage
		{
			Title = CreateOperationalTitle(cycle, "tabbed-page"),
			AutomationId = $"tabbed-page-{cycle}"
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

		if (!NativeTitleHasPayload(nativeItem))
			throw new InvalidOperationException("TabbedRenderer did not assign the expected native tab title payload.");

		var retainedPeer = RetainNativePeer(nativeItem);

		if (clearNativeTitleBeforeTeardown)
			ClearNativeTitle(nativeItem);

		((IElementHandler)renderer).DisconnectHandler();
		childHandler.DisconnectHandler();
		tabbedPage.Children.Clear();
		childPage.Handler = null;
		tabbedPage.Handler = null;
		nativeItem.Dispose();
		await DrainMainQueueAsync();

		return new TabbedCycleResult(retainedPeer, tabbedPage, childPage, childHandler);
	}

	static async Task<ShellCycleResult> CreateShellPeerAsync(
		int cycle,
		IMauiContext context,
		bool clearNativeTitleBeforeTeardown)
	{
		var shell = new Shell();
		var section = new ShellSection
		{
			Title = CreateOperationalTitle(cycle, "shell-section"),
			AutomationId = $"shell-section-{cycle}"
		};

		var sectionHandler = new ContextOnlyElementHandler(context);
		sectionHandler.SetVirtualView(section);
		section.Handler = sectionHandler;

		var renderer = new TestShellSectionRenderer(new FakeShellContext(shell));
		var nativeItem = await renderer.CreateTabBarItemAsync(section);

		if (nativeItem is null)
			throw new InvalidOperationException("ShellSectionRenderer did not assign a UITabBarItem.");

		if (!NativeTitleHasPayload(nativeItem))
			throw new InvalidOperationException("ShellSectionRenderer did not assign the expected native tab title payload.");

		var retainedPeer = RetainNativePeer(nativeItem);

		if (clearNativeTitleBeforeTeardown)
			ClearNativeTitle(nativeItem);

		renderer.ClearManagedFields();
		sectionHandler.DisconnectHandler();
		section.Title = null;
		nativeItem.Dispose();
		await DrainMainQueueAsync();

		return new ShellCycleResult(retainedPeer, shell, section, sectionHandler);
	}

	static string CreateOperationalTitle(int cycle, string role)
	{
		var header = $"Cycle {cycle:0000} {role} operational workspace. ";
		var sentence = "Regional dashboard, offline queue, policy exception, and fulfillment review tab. ";
		var targetChars = (int)(PayloadBytesPerTitle / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static bool NativeTitleHasPayload(UITabBarItem item) =>
		EstimateNativeTitleBytes(item) >= PayloadBytesPerTitle * 0.95;

	static void ClearNativeTitle(UITabBarItem item)
	{
		item.Title = null;
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

	static NativeTitleSnapshot GetNativeTitleSnapshot(RetainedTabPeer retainedPeer)
	{
		var peer = retainedPeer.TryGetPeer();
		if (peer is null)
			return new NativeTitleSnapshot(Alive: false, EstimatedTitleBytes: 0);

		return new NativeTitleSnapshot(Alive: true, EstimateNativeTitleBytes(peer));
	}

	static long EstimateNativeTitleBytes(UITabBarItem item) =>
		string.IsNullOrEmpty(item.Title) ? 0 : item.Title.Length * 2L;

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

	internal sealed record NativeTitleSnapshot(bool Alive, long EstimatedTitleBytes);

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

	internal sealed record TabbedCycleResult(
		RetainedTabPeer RetainedPeer,
		TabbedPage TabbedPage,
		ContentPage ChildPage,
		IPlatformViewHandler ChildHandler);

	internal sealed record ShellCycleResult(
		RetainedTabPeer RetainedPeer,
		Shell Shell,
		ShellSection Section,
		ContextOnlyElementHandler SectionHandler);

	internal sealed record CycleResult(
		RetainedTabPeer TabbedPeer,
		RetainedTabPeer ShellPeer,
		TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<TabbedPage> TabbedPage,
		WeakReference<ContentPage> ChildPage,
		WeakReference<IPlatformViewHandler> ChildHandler,
		WeakReference<Shell> Shell,
		WeakReference<ShellSection> Section,
		WeakReference<ContextOnlyElementHandler> SectionHandler)
	{
		public static TrackedCycle Create(
			int cycle,
			TabbedPage tabbedPage,
			ContentPage childPage,
			IPlatformViewHandler childHandler,
			Shell shell,
			ShellSection section,
			ContextOnlyElementHandler sectionHandler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<TabbedPage>(tabbedPage),
				new WeakReference<ContentPage>(childPage),
				new WeakReference<IPlatformViewHandler>(childHandler),
				new WeakReference<Shell>(shell),
				new WeakReference<ShellSection>(section),
				new WeakReference<ContextOnlyElementHandler>(sectionHandler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ExpectedNativePeers,
		int RetainedNativePeers,
		int NativePeersWithTitle,
		long EstimatedNativeTitleBytes,
		int AliveTabbedPages,
		int AliveChildPages,
		int AliveChildHandlers,
		int AliveShells,
		int AliveSections,
		int AliveSectionHandlers)
	{
		public int AlivePagesAndSections =>
			AliveTabbedPages +
			AliveChildPages +
			AliveShells +
			AliveSections;

		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedTabPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativePeers = 0;
			var nativePeersWithTitle = 0;
			long estimatedNativeTitleBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				var snapshot = GetNativeTitleSnapshot(retainedPeer);
				if (!snapshot.Alive)
					continue;

				retainedNativePeers++;
				if (snapshot.EstimatedTitleBytes > 0)
				{
					nativePeersWithTitle++;
					estimatedNativeTitleBytes += Math.Min(snapshot.EstimatedTitleBytes, PayloadBytesPerTitle);
				}
			}

			var aliveTabbedPages = 0;
			var aliveChildPages = 0;
			var aliveChildHandlers = 0;
			var aliveShells = 0;
			var aliveSections = 0;
			var aliveSectionHandlers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.TabbedPage.TryGetTarget(out _))
					aliveTabbedPages++;

				if (cycle.ChildPage.TryGetTarget(out _))
					aliveChildPages++;

				if (cycle.ChildHandler.TryGetTarget(out _))
					aliveChildHandlers++;

				if (cycle.Shell.TryGetTarget(out _))
					aliveShells++;

				if (cycle.Section.TryGetTarget(out _))
					aliveSections++;

				if (cycle.SectionHandler.TryGetTarget(out _))
					aliveSectionHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				tracked.Count * TitleSlotsPerCycle,
				retainedNativePeers,
				nativePeersWithTitle,
				estimatedNativeTitleBytes,
				aliveTabbedPages,
				aliveChildPages,
				aliveChildHandlers,
				aliveShells,
				aliveSections,
				aliveSectionHandlers);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerTitle,
	int TitleSlotsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativePeers == Control.ExpectedNativePeers &&
		Control.NativePeersWithTitle == 0 &&
		Control.AlivePagesAndSections <= 3 &&
		Control.AliveChildHandlers <= 1 &&
		Control.AliveSectionHandlers <= 1 &&
		Current.RetainedNativePeers == Current.ExpectedNativePeers &&
		Current.NativePeersWithTitle == Current.ExpectedNativePeers &&
		Current.EstimatedNativeTitleBytes >= Current.ExpectedNativePeers * PayloadKiBPerTitle * 1024L * 0.95 &&
		Current.AlivePagesAndSections <= 3 &&
		Current.AliveChildHandlers <= 1 &&
		Current.AliveSectionHandlers <= 1;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedNativeTitleBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedNativeTitleBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosTabBarItemTitleRetentionRepro",
			$"Tab title cycles per scenario: {Cycles}",
			$"Payload per native tab title: {PayloadKiBPerTitle} KiB",
			$"Native tab title slots per cycle: {TitleSlotsPerCycle}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native title payload: {controlMiB:N1} MiB",
			$"Current estimated retained native title payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var retainedMiB = result.EstimatedNativeTitleBytes / 1024d / 1024d;
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  expected native title peers: {result.ExpectedNativePeers}",
			$"  retained native title peers: {result.RetainedNativePeers}/{result.ExpectedNativePeers}",
			$"  native peers with assigned titles: {result.NativePeersWithTitle}/{result.ExpectedNativePeers}",
			$"  estimated retained native title bytes: {result.EstimatedNativeTitleBytes:N0}",
			$"  estimated retained native title MiB: {retainedMiB:N1}",
			$"  alive TabbedPages: {result.AliveTabbedPages}/{result.TrackedCycles}",
			$"  alive child pages: {result.AliveChildPages}/{result.TrackedCycles}",
			$"  alive child handlers: {result.AliveChildHandlers}/{result.TrackedCycles}",
			$"  alive Shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive ShellSections: {result.AliveSections}/{result.TrackedCycles}",
			$"  alive ShellSection handlers: {result.AliveSectionHandlers}/{result.TrackedCycles}",
			$"  alive pages/sections: {result.AlivePagesAndSections}/{result.ExpectedNativePeers}");
	}
}

internal sealed class TestShellSectionRenderer : ShellSectionRenderer
{
	public TestShellSectionRenderer(IShellContext context)
		: base(context)
	{
	}

	public async Task<UITabBarItem> CreateTabBarItemAsync(ShellSection section)
	{
		ShellSectionField(this) = section;
		UpdateTabBarItem();
		await ReproSession.DrainMainQueueAsync();
		return TabBarItem;
	}

	public void ClearManagedFields()
	{
		ShellSectionField(this) = null!;
		ContextField(this) = null!;
	}

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_shellSection")]
	static extern ref ShellSection ShellSectionField(ShellSectionRenderer renderer);

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_context")]
	static extern ref IShellContext ContextField(ShellSectionRenderer renderer);
}

internal sealed class ContextOnlyElementHandler : IElementHandler
{
	public ContextOnlyElementHandler(IMauiContext context)
	{
		MauiContext = context;
	}

	public object? PlatformView => null;

	public IElement? VirtualView { get; private set; }

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
