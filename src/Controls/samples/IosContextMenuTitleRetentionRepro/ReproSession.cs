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

namespace IosContextMenuTitleRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 256;
	internal const int ItemsPerMenu = 8;
	internal const int PayloadKiBPerTitle = 8;

	const long PayloadBytesPerTitle = PayloadKiBPerTitle * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedNativeMenu>> RetainedNativeMenus = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-context-menu-title-retention-results.txt");

	public static int TotalActions => Cycles * ItemsPerMenu;

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS context menu action title retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear UIAction.Title before retaining native context menu",
			context,
			clearActionTitles: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: MenuFlyoutItemHandler leaves UIAction.Title assigned",
			context,
			clearActionTitles: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeMenus);

		return new ReproReport(
			Cycles,
			ItemsPerMenu,
			PayloadKiBPerTitle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearActionTitles)
	{
		var retainedMenus = new List<RetainedNativeMenu>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 32 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, clearActionTitles);
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
		bool clearActionTitles)
	{
		var flyout = new MenuFlyout();
		var items = new MenuFlyoutItem[ItemsPerMenu];

		for (var itemIndex = 0; itemIndex < ItemsPerMenu; itemIndex++)
		{
			var item = new MenuFlyoutItem
			{
				Text = CreateActionTitle(cycle, itemIndex),
				Command = new Command(() => { })
			};

			flyout.Add(item);
			items[itemIndex] = item;
		}

		var flyoutHandler = flyout.ToHandler(context);
		var menu = (UIMenu)flyoutHandler.PlatformView!;
		var actions = GetActions(menu);

		if (actions.Count != ItemsPerMenu)
			throw new InvalidOperationException($"Expected {ItemsPerMenu} UIActions, found {actions.Count}.");

		if (CountActionsWithAssignedTitles(menu) != ItemsPerMenu)
			throw new InvalidOperationException("Context menu action title assignment did not populate every expected UIAction.");

		if (clearActionTitles)
			ClearActionTitles(menu);

		var retainedMenu = RetainNativeMenu(menu);

		var itemHandlers = items
			.Select(item => item.Handler ?? throw new InvalidOperationException("Menu item handler was not assigned."))
			.ToArray();

		var tracked = TrackedCycle.Create(cycle, flyout, flyoutHandler, items, itemHandlers);

		flyoutHandler.DisconnectHandler();
		await DrainMainQueueAsync();

		return new CycleResult(retainedMenu, tracked);
	}

	static string CreateActionTitle(int cycle, int itemIndex)
	{
		var header = $"Cycle {cycle:0000} action {itemIndex:00} offline workflow. ";
		var sentence = "Approve generated service notes, compliance evidence, and synced customer records. ";
		var targetChars = (int)(PayloadBytesPerTitle / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static IReadOnlyList<UIAction> GetActions(UIMenu menu)
	{
		var actions = new List<UIAction>();
		CollectActions(menu, actions);
		return actions;
	}

	static void CollectActions(UIMenuElement element, List<UIAction> actions)
	{
		if (element is UIAction action)
		{
			actions.Add(action);
			return;
		}

		if (element is UIMenu menu)
		{
			foreach (var child in menu.Children)
				CollectActions(child, actions);
		}
	}

	static void ClearActionTitles(UIMenu menu)
	{
		foreach (var action in GetActions(menu))
			action.Title = string.Empty;
	}

	static int CountActionsWithAssignedTitles(UIMenu menu)
	{
		var count = 0;

		foreach (var action in GetActions(menu))
		{
			if (EstimateTitleBytes(action) >= PayloadBytesPerTitle * 0.95)
				count++;
		}

		return count;
	}

	static long EstimateAssignedTitleBytes(UIMenu menu)
	{
		long bytes = 0;

		foreach (var action in GetActions(menu))
			bytes += Math.Min(EstimateTitleBytes(action), PayloadBytesPerTitle);

		return bytes;
	}

	static long EstimateTitleBytes(UIAction action)
	{
		return string.IsNullOrEmpty(action.Title) ? 0 : action.Title.Length * 2L;
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
		WeakReference<MenuFlyoutItem>[] Items,
		WeakReference<IElementHandler>[] ItemHandlers)
	{
		public static TrackedCycle Create(
			int cycle,
			MenuFlyout flyout,
			IElementHandler flyoutHandler,
			IReadOnlyList<MenuFlyoutItem> items,
			IReadOnlyList<IElementHandler> itemHandlers)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<MenuFlyout>(flyout),
				new WeakReference<IElementHandler>(flyoutHandler),
				items.Select(item => new WeakReference<MenuFlyoutItem>(item)).ToArray(),
				itemHandlers.Select(handler => new WeakReference<IElementHandler>(handler)).ToArray());
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeMenus,
		int RetainedNativeActions,
		int ActionsWithAssignedTitles,
		long EstimatedAssignedTitleBytes,
		int AliveFlyouts,
		int AliveFlyoutHandlers,
		int AliveMenuItems,
		int AliveItemHandlers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedNativeMenu> retainedMenus,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeMenus = 0;
			var retainedNativeActions = 0;
			var actionsWithAssignedTitles = 0;
			long estimatedAssignedTitleBytes = 0;

			foreach (var retainedMenu in retainedMenus)
			{
				var menu = retainedMenu.TryGetMenu();
				if (menu is null)
					continue;

				retainedNativeMenus++;
				retainedNativeActions += GetActions(menu).Count;
				actionsWithAssignedTitles += CountActionsWithAssignedTitles(menu);
				estimatedAssignedTitleBytes += EstimateAssignedTitleBytes(menu);
			}

			var aliveFlyouts = 0;
			var aliveFlyoutHandlers = 0;
			var aliveMenuItems = 0;
			var aliveItemHandlers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Flyout.TryGetTarget(out _))
					aliveFlyouts++;

				if (cycle.FlyoutHandler.TryGetTarget(out _))
					aliveFlyoutHandlers++;

				foreach (var item in cycle.Items)
				{
					if (item.TryGetTarget(out _))
						aliveMenuItems++;
				}

				foreach (var handler in cycle.ItemHandlers)
				{
					if (handler.TryGetTarget(out _))
						aliveItemHandlers++;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativeMenus,
				retainedNativeActions,
				actionsWithAssignedTitles,
				estimatedAssignedTitleBytes,
				aliveFlyouts,
				aliveFlyoutHandlers,
				aliveMenuItems,
				aliveItemHandlers);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int ItemsPerMenu,
	int PayloadKiBPerTitle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeMenus == Cycles &&
		Control.RetainedNativeActions == ReproSession.TotalActions &&
		Control.ActionsWithAssignedTitles == 0 &&
		Current.RetainedNativeMenus == Cycles &&
		Current.RetainedNativeActions == ReproSession.TotalActions &&
		Current.ActionsWithAssignedTitles == ReproSession.TotalActions &&
		Current.EstimatedAssignedTitleBytes >= ReproSession.TotalActions * PayloadKiBPerTitle * 1024L * 0.95 &&
		Current.AliveFlyouts <= 1 &&
		Current.AliveFlyoutHandlers <= 1 &&
		Current.AliveMenuItems <= ItemsPerMenu;

	public string ToText()
	{
		var currentMiB = Current.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosContextMenuTitleRetentionRepro",
			$"Cycles: {Cycles}",
			$"Items per context menu: {ItemsPerMenu}",
			$"Payload per action title: {PayloadKiBPerTitle} KiB",
			$"Total native actions: {ReproSession.TotalActions}",
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
			$"  retained native menus: {result.RetainedNativeMenus}/{result.TrackedCycles}",
			$"  retained native actions: {result.RetainedNativeActions}/{ReproSession.TotalActions}",
			$"  actions with assigned titles: {result.ActionsWithAssignedTitles}/{ReproSession.TotalActions}",
			$"  estimated retained native title bytes: {result.EstimatedAssignedTitleBytes:N0}",
			$"  estimated retained native title MiB: {nativeTitleMiB:N1}",
			$"  alive flyouts: {result.AliveFlyouts}/{result.TrackedCycles}",
			$"  alive flyout handlers: {result.AliveFlyoutHandlers}/{result.TrackedCycles}",
			$"  alive menu items: {result.AliveMenuItems}/{ReproSession.TotalActions}",
			$"  alive item handlers: {result.AliveItemHandlers}/{ReproSession.TotalActions}");
	}
}
