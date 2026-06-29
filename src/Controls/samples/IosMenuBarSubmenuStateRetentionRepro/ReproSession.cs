#nullable enable

using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using ObjCRuntime;
using UIKit;

namespace IosMenuBarSubmenuStateRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 128;
	internal const int SubmenusPerCycle = 8;
	internal const int PayloadKiBPerTitle = 8;
	internal const int SourceImagePixels = 192;

	const long PayloadBytesPerTitle = PayloadKiBPerTitle * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedNativeMenu>> RetainedNativeMenus = new();

	static readonly string AssetDirectory =
		Path.Combine(Path.GetTempPath(), "ios-menubar-submenu-state-retention-assets-" + Guid.NewGuid().ToString("N"));

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-menubar-submenu-state-retention-results.txt");

	public static int TotalSubmenus => Cycles * SubmenusPerCycle;

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS non-context menu bar submenu UIMenu state retention repro.");
		Directory.CreateDirectory(AssetDirectory);
		StaticMenuStore.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running short-value control scenario.");
		var control = await RunScenarioAsync(
			"control: retained native menu-bar UIMenus with short titles and no images",
			context,
			usePayloadState: false);

		WriteProgress("Running current MAUI payload scenario.");
		var current = await RunScenarioAsync(
			"current: MenuFlyoutSubItemHandler leaves UIMenu title/image state assigned",
			context,
			usePayloadState: true);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeMenus);

		return new ReproReport(
			Cycles,
			SubmenusPerCycle,
			PayloadKiBPerTitle,
			SourceImagePixels,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool usePayloadState)
	{
		var retainedMenus = new List<RetainedNativeMenu>(TotalSubmenus);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, usePayloadState);
			retainedMenus.AddRange(cycleResult.RetainedMenus);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativeMenus.Add(retainedMenus);
		ForceFullGc();

		return ScenarioResult.From(name, retainedMenus, tracked, StaticMenuStore.Count);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext context,
		bool usePayloadState)
	{
		var menuBar = new MenuBar();
		var menuBarItem = new MenuBarItem
		{
			Text = $"Workflow {cycle:0000}"
		};
		menuBar.Add(menuBarItem);
		menuBarItem.Parent = menuBar;

		var retainedMenus = new List<RetainedNativeMenu>(SubmenusPerCycle);
		var subitems = new MenuFlyoutSubItem[SubmenusPerCycle];
		var childItems = new MenuFlyoutItem[SubmenusPerCycle];
		var sources = new ImageSource?[SubmenusPerCycle];
		var subitemHandlers = new IElementHandler[SubmenusPerCycle];
		var childItemHandlers = new IElementHandler[SubmenusPerCycle];
		var nativeMenus = new UIMenu[SubmenusPerCycle];

		try
		{
			for (var submenuIndex = 0; submenuIndex < SubmenusPerCycle; submenuIndex++)
			{
				var subitem = CreateSubmenu(cycle, submenuIndex, usePayloadState, out var source, out var childItem);
				menuBarItem.Add(subitem);

				var handler = new MenuFlyoutSubItemHandler();
				handler.SetMauiContext(context);
				handler.SetVirtualView(subitem);

				if (handler.PlatformView is not UIMenu nativeMenu)
					throw new InvalidOperationException($"Expected a non-context native UIMenu, found {handler.PlatformView?.GetType().FullName ?? "null"}.");

				var childHandler = childItem.Handler
					?? throw new InvalidOperationException("Child menu item handler was not assigned.");

				subitems[submenuIndex] = subitem;
				childItems[submenuIndex] = childItem;
				sources[submenuIndex] = source;
				subitemHandlers[submenuIndex] = handler;
				childItemHandlers[submenuIndex] = childHandler;
				nativeMenus[submenuIndex] = nativeMenu;
				retainedMenus.Add(RetainNativeMenu(nativeMenu));
			}

			if (StaticMenuStore.Count != SubmenusPerCycle)
				throw new InvalidOperationException($"Expected {SubmenusPerCycle} static child command entries before reset, found {StaticMenuStore.Count}.");

			if (usePayloadState)
			{
				if (CountMenusWithPayloadTitles(nativeMenus) != SubmenusPerCycle)
					throw new InvalidOperationException("Payload title assignment did not populate every expected native submenu.");

				if (CountMenusWithImages(nativeMenus) != SubmenusPerCycle)
					throw new InvalidOperationException("Payload image assignment did not populate every expected native submenu.");
			}
			else
			{
				if (CountMenusWithPayloadTitles(nativeMenus) != 0)
					throw new InvalidOperationException("Short-title control unexpectedly created payload-sized native submenu titles.");

				if (CountMenusWithImages(nativeMenus) != 0)
					throw new InvalidOperationException("No-image control unexpectedly created native submenu images.");
			}

			var tracked = TrackedCycle.Create(
				cycle,
				menuBar,
				menuBarItem,
				subitems,
				childItems,
				sources,
				subitemHandlers,
				childItemHandlers);

			foreach (var handler in subitemHandlers)
				handler.DisconnectHandler();

			StaticMenuStore.Clear();
			await DrainMainQueueAsync();

			return new CycleResult(retainedMenus, tracked);
		}
		catch
		{
			StaticMenuStore.Clear();
			throw;
		}
	}

	static MenuFlyoutSubItem CreateSubmenu(
		int cycle,
		int submenuIndex,
		bool usePayloadState,
		out ImageSource? source,
		out MenuFlyoutItem childItem)
	{
		var subitem = new MenuFlyoutSubItem
		{
			Text = usePayloadState
				? CreatePayloadTitle(cycle, submenuIndex)
				: $"Group {cycle:0000}-{submenuIndex:00}"
		};

		if (usePayloadState)
		{
			var imagePath = CreateImageFile(cycle, submenuIndex);
			source = ImageSource.FromFile(imagePath);
			subitem.IconImageSource = source;
		}
		else
		{
			source = null;
		}

		childItem = new MenuFlyoutItem
		{
			Text = $"Open {cycle:0000}-{submenuIndex:00}",
			Command = new Command(() => { })
		};
		subitem.Add(childItem);

		return subitem;
	}

	static string CreatePayloadTitle(int cycle, int submenuIndex)
	{
		var header = $"Cycle {cycle:0000} menu bar submenu {submenuIndex:00} workflow group. ";
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

	static string CreateImageFile(int cycle, int submenuIndex)
	{
		var path = Path.Combine(AssetDirectory, $"menu-bar-submenu-{cycle:000}-{submenuIndex:00}.png");
		var format = new UIGraphicsImageRendererFormat
		{
			Opaque = true,
			Scale = 1
		};
		var renderer = new UIGraphicsImageRenderer(new CGSize(SourceImagePixels, SourceImagePixels), format);

		using var image = renderer.CreateImage(context =>
		{
			UIColor.FromRGB(
				(nfloat)((cycle * 43 + submenuIndex * 47) % 255) / 255f,
				(nfloat)((cycle * 89 + submenuIndex * 71) % 255) / 255f,
				(nfloat)((cycle * 131 + submenuIndex * 103) % 255) / 255f).SetFill();
			context.FillRect(new CGRect(0, 0, SourceImagePixels, SourceImagePixels));

			UIColor.White.SetStroke();
			context.CGContext.SetLineWidth(5);
			var inset = 18 + submenuIndex;
			context.CGContext.StrokeRect(new CGRect(inset, inset, SourceImagePixels - inset * 2, SourceImagePixels - inset * 2));
			context.CGContext.MoveTo(36, SourceImagePixels - 42 - submenuIndex);
			context.CGContext.AddLineToPoint(SourceImagePixels - 36, 42 + submenuIndex);
			context.CGContext.StrokePath();
		});

		using var data = image.AsPNG();
		using var url = NSUrl.FromFilename(path);
		if (data is null || !data.Save(url, false))
			throw new InvalidOperationException($"Failed to write repro image file: {path}");

		return path;
	}

	static int CountMenusWithPayloadTitles(IEnumerable<UIMenu> menus)
	{
		var count = 0;

		foreach (var menu in menus)
		{
			if (EstimateTitleBytes(menu) >= PayloadBytesPerTitle * 0.95)
				count++;
		}

		return count;
	}

	static int CountMenusWithImages(IEnumerable<UIMenu> menus)
	{
		var count = 0;

		foreach (var menu in menus)
		{
			if (menu.Image is not null)
				count++;
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

	static long EstimateAssignedImageBytes(IEnumerable<RetainedNativeMenu> retainedMenus)
	{
		long bytes = 0;

		foreach (var retainedMenu in retainedMenus)
		{
			if (retainedMenu.TryGetMenu()?.Image is { } image)
				bytes += EstimateImageBytes(image);
		}

		return bytes;
	}

	static long EstimateTitleBytes(UIMenu menu)
	{
		return string.IsNullOrEmpty(menu.Title) ? 0 : menu.Title.Length * 2L;
	}

	static long EstimateImageBytes(UIImage image)
	{
		var width = Math.Max(1, image.CGImage?.Width ?? (int)Math.Ceiling(image.Size.Width * image.CurrentScale));
		var height = Math.Max(1, image.CGImage?.Height ?? (int)Math.Ceiling(image.Size.Height * image.CurrentScale));
		return width * (long)height * 4;
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

	internal sealed record CycleResult(IReadOnlyList<RetainedNativeMenu> RetainedMenus, TrackedCycle Tracked);

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
		WeakReference<MenuFlyoutSubItem>[] Subitems,
		WeakReference<MenuFlyoutItem>[] ChildItems,
		WeakReference<ImageSource>[] Sources,
		WeakReference<IElementHandler>[] SubitemHandlers,
		WeakReference<IElementHandler>[] ChildItemHandlers)
	{
		public static TrackedCycle Create(
			int cycle,
			MenuBar menuBar,
			MenuBarItem menuBarItem,
			IReadOnlyList<MenuFlyoutSubItem> subitems,
			IReadOnlyList<MenuFlyoutItem> childItems,
			IReadOnlyList<ImageSource?> sources,
			IReadOnlyList<IElementHandler> subitemHandlers,
			IReadOnlyList<IElementHandler> childItemHandlers)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<MenuBar>(menuBar),
				new WeakReference<MenuBarItem>(menuBarItem),
				subitems.Select(item => new WeakReference<MenuFlyoutSubItem>(item)).ToArray(),
				childItems.Select(item => new WeakReference<MenuFlyoutItem>(item)).ToArray(),
				sources.Where(source => source is not null).Select(source => new WeakReference<ImageSource>(source!)).ToArray(),
				subitemHandlers.Select(handler => new WeakReference<IElementHandler>(handler)).ToArray(),
				childItemHandlers.Select(handler => new WeakReference<IElementHandler>(handler)).ToArray());
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeMenus,
		int MenusWithPayloadTitles,
		int MenusWithImages,
		long EstimatedAssignedTitleBytes,
		long EstimatedAssignedImageBytes,
		int AliveMenuBars,
		int AliveMenuBarItems,
		int AliveSubitems,
		int AliveChildItems,
		int AliveImageSources,
		int AliveSubitemHandlers,
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
			var menusWithPayloadTitles = 0;
			var menusWithImages = 0;

			foreach (var retainedMenu in retainedMenus)
			{
				var menu = retainedMenu.TryGetMenu();
				if (menu is null)
					continue;

				retainedNativeMenus++;

				if (EstimateTitleBytes(menu) >= PayloadBytesPerTitle * 0.95)
					menusWithPayloadTitles++;

				if (menu.Image is not null)
					menusWithImages++;
			}

			var aliveMenuBars = 0;
			var aliveMenuBarItems = 0;
			var aliveSubitems = 0;
			var aliveChildItems = 0;
			var aliveImageSources = 0;
			var aliveSubitemHandlers = 0;
			var aliveChildItemHandlers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.MenuBar.TryGetTarget(out _))
					aliveMenuBars++;

				if (cycle.MenuBarItem.TryGetTarget(out _))
					aliveMenuBarItems++;

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

				foreach (var source in cycle.Sources)
				{
					if (source.TryGetTarget(out _))
						aliveImageSources++;
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
				retainedNativeMenus,
				menusWithPayloadTitles,
				menusWithImages,
				EstimateAssignedTitleBytes(retainedMenus),
				EstimateAssignedImageBytes(retainedMenus),
				aliveMenuBars,
				aliveMenuBarItems,
				aliveSubitems,
				aliveChildItems,
				aliveImageSources,
				aliveSubitemHandlers,
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
	int SubmenusPerCycle,
	int PayloadKiBPerTitle,
	int SourceImagePixels,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeMenus == ReproSession.TotalSubmenus &&
		Control.MenusWithPayloadTitles == 0 &&
		Control.MenusWithImages == 0 &&
		Control.StaticMenuCountAfterScenario == 0 &&
		Current.RetainedNativeMenus == ReproSession.TotalSubmenus &&
		Current.MenusWithPayloadTitles == ReproSession.TotalSubmenus &&
		Current.MenusWithImages == ReproSession.TotalSubmenus &&
		Current.EstimatedAssignedTitleBytes >= ReproSession.TotalSubmenus * PayloadKiBPerTitle * 1024L * 0.95 &&
		Current.EstimatedAssignedImageBytes > Control.EstimatedAssignedImageBytes &&
		Current.AliveMenuBars <= 1 &&
		Current.AliveMenuBarItems <= 1 &&
		Current.AliveSubitems <= SubmenusPerCycle &&
		Current.AliveChildItems <= SubmenusPerCycle &&
		Current.AliveImageSources <= SubmenusPerCycle &&
		Current.AliveSubitemHandlers <= SubmenusPerCycle &&
		Current.AliveChildItemHandlers <= SubmenusPerCycle &&
		Current.StaticMenuCountAfterScenario == 0;

	public string ToText()
	{
		var currentTitleMiB = Current.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var controlTitleMiB = Control.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var currentImageMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlImageMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosMenuBarSubmenuStateRetentionRepro",
			$"Cycles: {Cycles}",
			$"Native menu-bar submenus per cycle: {SubmenusPerCycle}",
			$"Payload per submenu title: {PayloadKiBPerTitle} KiB",
			$"Source image size: {SourceImagePixels} x {SourceImagePixels} pixels",
			$"Total retained native submenus: {ReproSession.TotalSubmenus}",
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
			$"Control estimated retained native image payload: {controlImageMiB:N1} MiB",
			$"Current estimated retained native image payload: {currentImageMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeTitleMiB = result.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var nativeImageMiB = result.EstimatedAssignedImageBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native submenus: {result.RetainedNativeMenus}/{ReproSession.TotalSubmenus}",
			$"  submenus with payload-sized titles: {result.MenusWithPayloadTitles}/{ReproSession.TotalSubmenus}",
			$"  submenus with assigned images: {result.MenusWithImages}/{ReproSession.TotalSubmenus}",
			$"  estimated retained native title bytes: {result.EstimatedAssignedTitleBytes:N0}",
			$"  estimated retained native title MiB: {nativeTitleMiB:N1}",
			$"  estimated retained native image bytes: {result.EstimatedAssignedImageBytes:N0}",
			$"  estimated retained native image MiB: {nativeImageMiB:N1}",
			$"  alive menu bars: {result.AliveMenuBars}/{result.TrackedCycles}",
			$"  alive menu bar items: {result.AliveMenuBarItems}/{result.TrackedCycles}",
			$"  alive subitems: {result.AliveSubitems}/{ReproSession.TotalSubmenus}",
			$"  alive child items: {result.AliveChildItems}/{ReproSession.TotalSubmenus}",
			$"  alive image sources: {result.AliveImageSources}/{ReproSession.TotalSubmenus}",
			$"  alive subitem handlers: {result.AliveSubitemHandlers}/{ReproSession.TotalSubmenus}",
			$"  alive child item handlers: {result.AliveChildItemHandlers}/{ReproSession.TotalSubmenus}",
			$"  static menu dictionary count after scenario: {result.StaticMenuCountAfterScenario}");
	}
}
