#nullable enable

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Handlers;
using ObjCRuntime;
using UIKit;

namespace IosNavigationItemTitleRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerTitle = 128;

	const long PayloadBytesPerTitle = PayloadKiBPerTitle * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly MethodInfo CreateViewControllerForPageMethod =
		typeof(NavigationRenderer).GetMethod("CreateViewControllerForPage", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(NavigationRenderer).FullName, "CreateViewControllerForPage");

	static readonly List<IReadOnlyList<RetainedNativeNavigationItem>> RetainedNativeNavigationItems = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-navigationitem-title-retention-results.txt");

	public static int TotalTitleSlots => Cycles * 2;

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS navigation item title retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear UINavigationItem title slots before retaining native navigation item",
			context,
			clearNativeTitles: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: NavigationRenderer leaves UINavigationItem title slots assigned",
			context,
			clearNativeTitles: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeNavigationItems);

		return new ReproReport(
			Cycles,
			PayloadKiBPerTitle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeTitles)
	{
		var retainedItems = new List<RetainedNativeNavigationItem>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, clearNativeTitles);
			retainedItems.Add(cycleResult.RetainedNavigationItem);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativeNavigationItems.Add(retainedItems);
		ForceFullGc();

		return ScenarioResult.From(name, retainedItems, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext context,
		bool clearNativeTitles)
	{
		var rootPage = new ContentPage
		{
			Title = $"Root {cycle:0000}",
			Content = new Label { Text = $"Root {cycle:0000}" }
		};
		var navPage = new NavigationPage(rootPage);
		var renderer = new NavigationRenderer();

		((IElementHandler)renderer).SetMauiContext(context);
		renderer.SetElement(navPage);
		renderer.LoadViewIfNeeded();

		SetRealisticNavigationBounds(renderer);

		var titlePage = new ContentPage
		{
			Title = CreateLargeTitle(cycle, "Page"),
			Content = new Label { Text = $"Orders {cycle:0000}" }
		};
		NavigationPage.SetBackButtonTitle(titlePage, CreateLargeTitle(cycle, "Back"));

		var pageController = CreateViewControllerForPage(renderer, titlePage);
		var navigationItem = pageController.NavigationItem
			?? throw new InvalidOperationException("NavigationRenderer did not create a UINavigationItem.");

		if (CountAssignedPayloadTitles(navigationItem) != 2)
			throw new InvalidOperationException("NavigationRenderer did not assign both payload-sized native title slots.");

		if (clearNativeTitles)
			ClearNativeTitles(navigationItem);

		var retainedNavigationItem = RetainNativeNavigationItem(navigationItem);

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
			retainedNavigationItem,
			TrackedCycle.Create(cycle, renderer, navPage, rootPage, titlePage));
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

	static string CreateLargeTitle(int cycle, string slot)
	{
		var header = $"{slot} navigation label {cycle:0000}. ";
		var sentence = "Generated case workspace, offline account summary, approval route, and review history. ";
		var targetChars = (int)(PayloadBytesPerTitle / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static void ClearNativeTitles(UINavigationItem navigationItem)
	{
		navigationItem.Title = string.Empty;

		if (navigationItem.BackBarButtonItem is { } backItem)
			backItem.Title = string.Empty;
	}

	static int CountAssignedPayloadTitles(UINavigationItem navigationItem)
	{
		var count = 0;

		if (EstimateTitleBytes(navigationItem.Title) >= PayloadBytesPerTitle * 0.95)
			count++;

		if (EstimateTitleBytes(navigationItem.BackBarButtonItem?.Title) >= PayloadBytesPerTitle * 0.95)
			count++;

		return count;
	}

	static long EstimateAssignedTitleBytes(UINavigationItem navigationItem)
	{
		return Math.Min(EstimateTitleBytes(navigationItem.Title), PayloadBytesPerTitle) +
			Math.Min(EstimateTitleBytes(navigationItem.BackBarButtonItem?.Title), PayloadBytesPerTitle);
	}

	static long EstimateTitleBytes(string? title)
	{
		return string.IsNullOrEmpty(title) ? 0 : title.Length * 2L;
	}

	static RetainedNativeNavigationItem RetainNativeNavigationItem(UINavigationItem navigationItem)
	{
		var handle = navigationItem.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UINavigationItem with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedNativeNavigationItem(retained);
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
			NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(75);
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

	internal sealed record CycleResult(RetainedNativeNavigationItem RetainedNavigationItem, TrackedCycle Tracked);

	internal sealed class RetainedNativeNavigationItem
	{
		public RetainedNativeNavigationItem(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UINavigationItem? TryGetNavigationItem()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UINavigationItem>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<NavigationRenderer> Renderer,
		WeakReference<NavigationPage> NavigationPage,
		WeakReference<ContentPage> RootPage,
		WeakReference<ContentPage> TitlePage)
	{
		public static TrackedCycle Create(
			int cycle,
			NavigationRenderer renderer,
			NavigationPage navigationPage,
			ContentPage rootPage,
			ContentPage titlePage)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<NavigationRenderer>(renderer),
				new WeakReference<NavigationPage>(navigationPage),
				new WeakReference<ContentPage>(rootPage),
				new WeakReference<ContentPage>(titlePage));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeNavigationItems,
		int AssignedPayloadTitleSlots,
		long EstimatedAssignedTitleBytes,
		int AliveRenderers,
		int AliveNavigationPages,
		int AliveRootPages,
		int AliveTitlePages)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedNativeNavigationItem> retainedItems,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeNavigationItems = 0;
			var assignedPayloadTitleSlots = 0;
			long estimatedAssignedTitleBytes = 0;

			foreach (var retainedItem in retainedItems)
			{
				var navigationItem = retainedItem.TryGetNavigationItem();
				if (navigationItem is null)
					continue;

				retainedNativeNavigationItems++;
				assignedPayloadTitleSlots += CountAssignedPayloadTitles(navigationItem);
				estimatedAssignedTitleBytes += EstimateAssignedTitleBytes(navigationItem);
			}

			var aliveRenderers = 0;
			var aliveNavigationPages = 0;
			var aliveRootPages = 0;
			var aliveTitlePages = 0;

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
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativeNavigationItems,
				assignedPayloadTitleSlots,
				estimatedAssignedTitleBytes,
				aliveRenderers,
				aliveNavigationPages,
				aliveRootPages,
				aliveTitlePages);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerTitle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeNavigationItems == Cycles &&
		Control.AssignedPayloadTitleSlots == 0 &&
		Current.RetainedNativeNavigationItems == Cycles &&
		Current.AssignedPayloadTitleSlots == ReproSession.TotalTitleSlots &&
		Current.EstimatedAssignedTitleBytes >= ReproSession.TotalTitleSlots * PayloadKiBPerTitle * 1024L * 0.95 &&
		Current.AliveTitlePages <= 1;

	public string ToText()
	{
		var currentMiB = Current.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosNavigationItemTitleRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per native title slot: {PayloadKiBPerTitle} KiB",
			$"Native title slots per cycle: 2",
			$"Total native title slots: {ReproSession.TotalTitleSlots}",
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
		var nativeTitleMiB = result.EstimatedAssignedTitleBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native navigation items: {result.RetainedNativeNavigationItems}/{result.TrackedCycles}",
			$"  assigned payload-sized title slots: {result.AssignedPayloadTitleSlots}/{ReproSession.TotalTitleSlots}",
			$"  estimated retained native title bytes: {result.EstimatedAssignedTitleBytes:N0}",
			$"  estimated retained native title MiB: {nativeTitleMiB:N1}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive navigation pages: {result.AliveNavigationPages}/{result.TrackedCycles}",
			$"  alive root pages: {result.AliveRootPages}/{result.TrackedCycles}",
			$"  alive title pages: {result.AliveTitlePages}/{result.TrackedCycles}");
	}
}
