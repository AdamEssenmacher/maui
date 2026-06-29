#nullable enable

using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using ObjCRuntime;
using UIKit;

namespace IosMenuBarItemTitleRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 1024;
	internal const int PayloadKiBPerTitle = 8;

	const long PayloadBytesPerTitle = PayloadKiBPerTitle * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<RetainedNativeMenu> RetainedNativeMenus = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-menubaritem-title-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS menu bar item UIMenu title retention repro.");
		StaticMenuStore.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running short-title control scenario.");
		var control = await RunScenarioAsync(
			"control: retained native menu-bar UIMenus with short titles",
			context,
			usePayloadTitle: false);

		WriteProgress("Running current MAUI payload scenario.");
		var current = await RunScenarioAsync(
			"current: MenuBarItemHandler leaves UIMenu title state assigned",
			context,
			usePayloadTitle: true);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeMenus);

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
		bool usePayloadTitle)
	{
		var retainedMenus = new List<RetainedNativeMenu>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 128 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, usePayloadTitle);
			retainedMenus.Add(cycleResult.RetainedMenu);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativeMenus.AddRange(retainedMenus);
		ForceFullGc();

		return ScenarioResult.From(name, retainedMenus, tracked, StaticMenuStore.Count);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext context,
		bool usePayloadTitle)
	{
		var menuBar = new MenuBar();
		var menuBarItem = new MenuBarItem
		{
			Text = usePayloadTitle
				? CreatePayloadTitle(cycle)
				: $"Workflow {cycle:0000}"
		};
		menuBar.Add(menuBarItem);
		menuBarItem.Parent = menuBar;

		var childItem = new MenuFlyoutItem
		{
			Text = $"Open {cycle:0000}",
			Command = new Command(() => { })
		};
		menuBarItem.Add(childItem);

		try
		{
			var handler = new MenuBarItemHandler();
			handler.SetMauiContext(context);
			handler.SetVirtualView(menuBarItem);

			if (handler.PlatformView is not UIMenu nativeMenu)
				throw new InvalidOperationException($"Expected a native root UIMenu, found {handler.PlatformView?.GetType().FullName ?? "null"}.");

			var childHandler = childItem.Handler
				?? throw new InvalidOperationException("Child menu item handler was not assigned.");

			if (StaticMenuStore.Count != 1)
				throw new InvalidOperationException($"Expected one static child command entry before reset, found {StaticMenuStore.Count}.");

			if (usePayloadTitle)
			{
				if (EstimateTitleBytes(nativeMenu) < PayloadBytesPerTitle * 0.95)
					throw new InvalidOperationException("Payload title assignment did not populate the native menu-bar UIMenu.");
			}
			else if (EstimateTitleBytes(nativeMenu) >= PayloadBytesPerTitle * 0.95)
			{
				throw new InvalidOperationException("Short-title control unexpectedly created a payload-sized native menu title.");
			}

			var retainedMenu = RetainNativeMenu(nativeMenu);
			IElementHandler menuBarItemHandler = handler;
			var tracked = TrackedCycle.Create(cycle, menuBar, menuBarItem, childItem, menuBarItemHandler, childHandler);

			menuBarItemHandler.DisconnectHandler();
			StaticMenuStore.Clear();
			if (cycle % 16 == 15)
				await DrainMainQueueAsync();

			return new CycleResult(retainedMenu, tracked);
		}
		catch
		{
			StaticMenuStore.Clear();
			throw;
		}
	}

	static string CreatePayloadTitle(int cycle)
	{
		var header = $"Cycle {cycle:0000} menu bar workflow group. ";
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

	static int CountMenusWithPayloadTitles(IEnumerable<RetainedNativeMenu> retainedMenus)
	{
		var count = 0;

		foreach (var retainedMenu in retainedMenus)
		{
			if (retainedMenu.TryGetMenu() is { } menu &&
				EstimateTitleBytes(menu) >= PayloadBytesPerTitle * 0.95)
			{
				count++;
			}
		}

		return count;
	}

	static long EstimateAssignedTitleBytes(IEnumerable<RetainedNativeMenu> retainedMenus)
	{
		long bytes = 0;

		foreach (var retainedMenu in retainedMenus)
		{
			if (retainedMenu.TryGetMenu() is { } menu)
				bytes += Math.Min(EstimateTitleBytes(menu), PayloadBytesPerTitle);
		}

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
		WeakReference<MenuBar> MenuBar,
		WeakReference<MenuBarItem> MenuBarItem,
		WeakReference<MenuFlyoutItem> ChildItem,
		WeakReference<IElementHandler> MenuBarItemHandler,
		WeakReference<IElementHandler> ChildItemHandler)
	{
		public static TrackedCycle Create(
			int cycle,
			MenuBar menuBar,
			MenuBarItem menuBarItem,
			MenuFlyoutItem childItem,
			IElementHandler menuBarItemHandler,
			IElementHandler childItemHandler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<MenuBar>(menuBar),
				new WeakReference<MenuBarItem>(menuBarItem),
				new WeakReference<MenuFlyoutItem>(childItem),
				new WeakReference<IElementHandler>(menuBarItemHandler),
				new WeakReference<IElementHandler>(childItemHandler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeMenus,
		int MenusWithPayloadTitles,
		long EstimatedAssignedTitleBytes,
		int AliveMenuBars,
		int AliveMenuBarItems,
		int AliveChildItems,
		int AliveMenuBarItemHandlers,
		int AliveChildItemHandlers,
		int StaticMenuCountAfterScenario)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedNativeMenu> retainedMenus,
			IReadOnlyList<TrackedCycle> tracked,
			int staticMenuCountAfterScenario)
		{
			var retainedNativeMenus = 0;
			foreach (var retainedMenu in retainedMenus)
			{
				if (retainedMenu.TryGetMenu() is not null)
					retainedNativeMenus++;
			}

			var aliveMenuBars = 0;
			var aliveMenuBarItems = 0;
			var aliveChildItems = 0;
			var aliveMenuBarItemHandlers = 0;
			var aliveChildItemHandlers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.MenuBar.TryGetTarget(out _))
					aliveMenuBars++;

				if (cycle.MenuBarItem.TryGetTarget(out _))
					aliveMenuBarItems++;

				if (cycle.ChildItem.TryGetTarget(out _))
					aliveChildItems++;

				if (cycle.MenuBarItemHandler.TryGetTarget(out _))
					aliveMenuBarItemHandlers++;

				if (cycle.ChildItemHandler.TryGetTarget(out _))
					aliveChildItemHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativeMenus,
				CountMenusWithPayloadTitles(retainedMenus),
				EstimateAssignedTitleBytes(retainedMenus),
				aliveMenuBars,
				aliveMenuBarItems,
				aliveChildItems,
				aliveMenuBarItemHandlers,
				aliveChildItemHandlers,
				staticMenuCountAfterScenario);
		}
	}

	static class StaticMenuStore
	{
		static readonly FieldInfo MenusField =
			typeof(MenuFlyoutItemHandler).GetField("menus", BindingFlags.NonPublic | BindingFlags.Static)
			?? throw new InvalidOperationException("Missing MenuFlyoutItemHandler.menus.");

		public static int Count => GetDictionary().Count;

		public static void Clear()
		{
			GetDictionary().Clear();
		}

		static IDictionary GetDictionary()
		{
			return MenusField.GetValue(null) as IDictionary
				?? throw new InvalidOperationException("MenuFlyoutItemHandler.menus was null.");
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
		Control.RetainedNativeMenus == Cycles &&
		Control.MenusWithPayloadTitles == 0 &&
		Control.StaticMenuCountAfterScenario == 0 &&
		Current.RetainedNativeMenus == Cycles &&
		Current.MenusWithPayloadTitles == Cycles &&
		Current.EstimatedAssignedTitleBytes >= Cycles * PayloadKiBPerTitle * 1024L * 0.95 &&
		Current.AliveMenuBars <= 1 &&
		Current.AliveMenuBarItems <= 1 &&
		Current.AliveChildItems <= 1 &&
		Current.AliveMenuBarItemHandlers <= 1 &&
		Current.AliveChildItemHandlers <= 1 &&
		Current.StaticMenuCountAfterScenario == 0;

	public string ToText()
	{
		var currentTitleMiB = Current.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var controlTitleMiB = Control.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosMenuBarItemTitleRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per menu-bar item title: {PayloadKiBPerTitle} KiB",
			$"Total retained native root menus: {Cycles}",
			"Note: child menu commands use short titles/no images and the static MenuFlyoutItemHandler.menus dictionary is cleared after every cycle, so C026 and C249 are not part of this proof.",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native title payload: {controlTitleMiB:N1} MiB",
			$"Current estimated retained native title payload: {currentTitleMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeTitleMiB = result.EstimatedAssignedTitleBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native root menus: {result.RetainedNativeMenus}/{result.TrackedCycles}",
			$"  root menus with payload-sized titles: {result.MenusWithPayloadTitles}/{result.TrackedCycles}",
			$"  estimated retained native title bytes: {result.EstimatedAssignedTitleBytes:N0}",
			$"  estimated retained native title MiB: {nativeTitleMiB:N1}",
			$"  alive menu bars: {result.AliveMenuBars}/{result.TrackedCycles}",
			$"  alive menu bar items: {result.AliveMenuBarItems}/{result.TrackedCycles}",
			$"  alive child items: {result.AliveChildItems}/{result.TrackedCycles}",
			$"  alive menu bar item handlers: {result.AliveMenuBarItemHandlers}/{result.TrackedCycles}",
			$"  alive child item handlers: {result.AliveChildItemHandlers}/{result.TrackedCycles}",
			$"  static menu dictionary count after scenario: {result.StaticMenuCountAfterScenario}");
	}
}
