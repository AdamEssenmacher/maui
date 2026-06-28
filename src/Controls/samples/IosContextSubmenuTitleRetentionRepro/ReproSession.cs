#nullable enable

using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using ObjCRuntime;
using UIKit;

namespace IosContextSubmenuTitleRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 256;
	internal const int SubmenusPerMenu = 8;
	internal const int PayloadKiBPerTitle = 8;

	const long PayloadBytesPerTitle = PayloadKiBPerTitle * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedNativeMenu>> RetainedNativeMenus = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-context-submenu-title-retention-results.txt");

	public static int TotalSubmenus => Cycles * SubmenusPerMenu;

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS context submenu title retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: retained context submenus with short UIMenu.Title values",
			context,
			useLargeSubmenuTitles: false);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: MenuFlyoutSubItemHandler leaves UIMenu.Title assigned",
			context,
			useLargeSubmenuTitles: true);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeMenus);

		return new ReproReport(
			Cycles,
			SubmenusPerMenu,
			PayloadKiBPerTitle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool useLargeSubmenuTitles)
	{
		var retainedMenus = new List<RetainedNativeMenu>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 32 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, useLargeSubmenuTitles);
			retainedMenus.Add(cycleResult.RetainedMenu);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativeMenus.Add(retainedMenus);
		ForceFullGc();

		return ScenarioResult.From(name, retainedMenus, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext context,
		bool useLargeSubmenuTitles)
	{
		var flyout = new MenuFlyout();
		var subitems = new MenuFlyoutSubItem[SubmenusPerMenu];
		var childItems = new MenuFlyoutItem[SubmenusPerMenu];

		for (var submenuIndex = 0; submenuIndex < SubmenusPerMenu; submenuIndex++)
		{
			var childItem = new MenuFlyoutItem
			{
				Text = $"Open {cycle:0000}-{submenuIndex:00}",
				Command = new Command(() => { })
			};

			var subitem = new MenuFlyoutSubItem
			{
				Text = useLargeSubmenuTitles
					? CreateLargeSubmenuTitle(cycle, submenuIndex)
					: CreateShortSubmenuTitle(cycle, submenuIndex)
			};

			subitem.Add(childItem);
			flyout.Add(subitem);

			subitems[submenuIndex] = subitem;
			childItems[submenuIndex] = childItem;
		}

		var flyoutHandler = flyout.ToHandler(context);
		var rootMenu = (UIMenu)flyoutHandler.PlatformView!;
		var submenus = GetSubmenus(rootMenu);

		if (submenus.Count != SubmenusPerMenu)
			throw new InvalidOperationException($"Expected {SubmenusPerMenu} native submenus, found {submenus.Count}.");

		if (useLargeSubmenuTitles && CountSubmenusWithPayloadTitles(rootMenu) != SubmenusPerMenu)
			throw new InvalidOperationException("Context submenu title assignment did not populate every expected UIMenu.");

		var retainedMenu = RetainNativeMenu(rootMenu);

		var subitemHandlers = subitems
			.Select(item => item.Handler ?? throw new InvalidOperationException("Submenu handler was not assigned."))
			.ToArray();

		var childItemHandlers = childItems
			.Select(item => item.Handler ?? throw new InvalidOperationException("Child menu item handler was not assigned."))
			.ToArray();

		var tracked = TrackedCycle.Create(cycle, flyout, flyoutHandler, subitems, childItems, subitemHandlers, childItemHandlers);

		flyoutHandler.DisconnectHandler();
		await DrainMainQueueAsync();

		return new CycleResult(retainedMenu, tracked);
	}

	static string CreateLargeSubmenuTitle(int cycle, int submenuIndex)
	{
		var header = $"Cycle {cycle:0000} submenu {submenuIndex:00} workflow group. ";
		var sentence = "Review generated options, synced records, compliance evidence, and offline customer notes. ";
		var targetChars = (int)(PayloadBytesPerTitle / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static string CreateShortSubmenuTitle(int cycle, int submenuIndex)
	{
		return $"Group {cycle:0000}-{submenuIndex:00}";
	}

	static IReadOnlyList<UIMenu> GetSubmenus(UIMenu rootMenu)
	{
		var submenus = new List<UIMenu>();

		foreach (var child in rootMenu.Children)
			CollectSubmenus(child, submenus);

		return submenus;
	}

	static void CollectSubmenus(UIMenuElement element, List<UIMenu> submenus)
	{
		if (element is not UIMenu menu)
			return;

		submenus.Add(menu);

		foreach (var child in menu.Children)
			CollectSubmenus(child, submenus);
	}

	static int CountSubmenusWithPayloadTitles(UIMenu rootMenu)
	{
		var count = 0;

		foreach (var submenu in GetSubmenus(rootMenu))
		{
			if (EstimateTitleBytes(submenu) >= PayloadBytesPerTitle * 0.95)
				count++;
		}

		return count;
	}

	static long EstimateAssignedTitleBytes(UIMenu rootMenu)
	{
		long bytes = 0;

		foreach (var submenu in GetSubmenus(rootMenu))
			bytes += Math.Min(EstimateTitleBytes(submenu), PayloadBytesPerTitle);

		return bytes;
	}

	static long EstimateTitleBytes(UIMenu menu)
	{
		return string.IsNullOrEmpty(menu.Title) ? 0 : menu.Title.Length * 2L;
	}

	static RetainedNativeMenu RetainNativeMenu(UIMenu menu)
	{
		var handle = menu.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UIMenu with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedNativeMenu(retained);
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

	internal sealed record CycleResult(RetainedNativeMenu RetainedMenu, TrackedCycle Tracked);

	internal sealed class RetainedNativeMenu
	{
		public RetainedNativeMenu(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UIMenu? TryGetMenu()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UIMenu>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<MenuFlyout> Flyout,
		WeakReference<IElementHandler> FlyoutHandler,
		WeakReference<MenuFlyoutSubItem>[] Subitems,
		WeakReference<MenuFlyoutItem>[] ChildItems,
		WeakReference<IElementHandler>[] SubitemHandlers,
		WeakReference<IElementHandler>[] ChildItemHandlers)
	{
		public static TrackedCycle Create(
			int cycle,
			MenuFlyout flyout,
			IElementHandler flyoutHandler,
			IReadOnlyList<MenuFlyoutSubItem> subitems,
			IReadOnlyList<MenuFlyoutItem> childItems,
			IReadOnlyList<IElementHandler> subitemHandlers,
			IReadOnlyList<IElementHandler> childItemHandlers)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<MenuFlyout>(flyout),
				new WeakReference<IElementHandler>(flyoutHandler),
				subitems.Select(item => new WeakReference<MenuFlyoutSubItem>(item)).ToArray(),
				childItems.Select(item => new WeakReference<MenuFlyoutItem>(item)).ToArray(),
				subitemHandlers.Select(handler => new WeakReference<IElementHandler>(handler)).ToArray(),
				childItemHandlers.Select(handler => new WeakReference<IElementHandler>(handler)).ToArray());
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeRootMenus,
		int RetainedNativeSubmenus,
		int SubmenusWithPayloadTitles,
		long EstimatedAssignedTitleBytes,
		int AliveFlyouts,
		int AliveFlyoutHandlers,
		int AliveSubitems,
		int AliveChildItems,
		int AliveSubitemHandlers,
		int AliveChildItemHandlers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedNativeMenu> retainedMenus,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeRootMenus = 0;
			var retainedNativeSubmenus = 0;
			var submenusWithPayloadTitles = 0;
			long estimatedAssignedTitleBytes = 0;

			foreach (var retainedMenu in retainedMenus)
			{
				var menu = retainedMenu.TryGetMenu();
				if (menu is null)
					continue;

				retainedNativeRootMenus++;
				retainedNativeSubmenus += GetSubmenus(menu).Count;
				submenusWithPayloadTitles += CountSubmenusWithPayloadTitles(menu);
				estimatedAssignedTitleBytes += EstimateAssignedTitleBytes(menu);
			}

			var aliveFlyouts = 0;
			var aliveFlyoutHandlers = 0;
			var aliveSubitems = 0;
			var aliveChildItems = 0;
			var aliveSubitemHandlers = 0;
			var aliveChildItemHandlers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Flyout.TryGetTarget(out _))
					aliveFlyouts++;

				if (cycle.FlyoutHandler.TryGetTarget(out _))
					aliveFlyoutHandlers++;

				foreach (var item in cycle.Subitems)
				{
					if (item.TryGetTarget(out _))
						aliveSubitems++;
				}

				foreach (var item in cycle.ChildItems)
				{
					if (item.TryGetTarget(out _))
						aliveChildItems++;
				}

				foreach (var handler in cycle.SubitemHandlers)
				{
					if (handler.TryGetTarget(out _))
						aliveSubitemHandlers++;
				}

				foreach (var handler in cycle.ChildItemHandlers)
				{
					if (handler.TryGetTarget(out _))
						aliveChildItemHandlers++;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativeRootMenus,
				retainedNativeSubmenus,
				submenusWithPayloadTitles,
				estimatedAssignedTitleBytes,
				aliveFlyouts,
				aliveFlyoutHandlers,
				aliveSubitems,
				aliveChildItems,
				aliveSubitemHandlers,
				aliveChildItemHandlers);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int SubmenusPerMenu,
	int PayloadKiBPerTitle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeRootMenus == Cycles &&
		Control.RetainedNativeSubmenus == ReproSession.TotalSubmenus &&
		Control.SubmenusWithPayloadTitles == 0 &&
		Current.RetainedNativeRootMenus == Cycles &&
		Current.RetainedNativeSubmenus == ReproSession.TotalSubmenus &&
		Current.SubmenusWithPayloadTitles == ReproSession.TotalSubmenus &&
		Current.EstimatedAssignedTitleBytes >= ReproSession.TotalSubmenus * PayloadKiBPerTitle * 1024L * 0.95 &&
		Current.AliveFlyouts <= 1 &&
		Current.AliveFlyoutHandlers <= 1 &&
		Current.AliveSubitems <= SubmenusPerMenu &&
		Current.AliveChildItems <= SubmenusPerMenu;

	public string ToText()
	{
		var currentMiB = Current.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosContextSubmenuTitleRetentionRepro",
			$"Cycles: {Cycles}",
			$"Submenus per context menu: {SubmenusPerMenu}",
			$"Payload per submenu title: {PayloadKiBPerTitle} KiB",
			$"Total native submenus: {ReproSession.TotalSubmenus}",
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
			$"  retained native root menus: {result.RetainedNativeRootMenus}/{result.TrackedCycles}",
			$"  retained native submenus: {result.RetainedNativeSubmenus}/{ReproSession.TotalSubmenus}",
			$"  submenus with payload-sized titles: {result.SubmenusWithPayloadTitles}/{ReproSession.TotalSubmenus}",
			$"  estimated retained native title bytes: {result.EstimatedAssignedTitleBytes:N0}",
			$"  estimated retained native title MiB: {nativeTitleMiB:N1}",
			$"  alive flyouts: {result.AliveFlyouts}/{result.TrackedCycles}",
			$"  alive flyout handlers: {result.AliveFlyoutHandlers}/{result.TrackedCycles}",
			$"  alive subitems: {result.AliveSubitems}/{ReproSession.TotalSubmenus}",
			$"  alive child items: {result.AliveChildItems}/{ReproSession.TotalSubmenus}",
			$"  alive subitem handlers: {result.AliveSubitemHandlers}/{ReproSession.TotalSubmenus}",
			$"  alive child item handlers: {result.AliveChildItemHandlers}/{ReproSession.TotalSubmenus}");
	}
}
