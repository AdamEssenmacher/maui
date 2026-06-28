#nullable enable

using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace IosContextSubmenuImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 100;
	internal const int SubmenusPerMenu = 8;
	internal const int SourceImagePixels = 192;

	static readonly List<RetainedMenu> RetainedNativeMenus = new();

	static readonly string AssetDirectory =
		Path.Combine(Path.GetTempPath(), "ios-context-submenu-image-retention-assets-" + Guid.NewGuid().ToString("N"));

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-context-submenu-image-retention-results.txt");

	public static int TotalSubmenus => Cycles * SubmenusPerMenu;

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS context submenu image retention repro.");
		Directory.CreateDirectory(AssetDirectory);
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: retain native context submenus without submenu images",
			context,
			assignSubmenuImages: false);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: MenuFlyoutSubItemHandler assigns UIMenu.Image",
			context,
			assignSubmenuImages: true);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeMenus);

		return new ReproReport(
			Cycles,
			SubmenusPerMenu,
			SourceImagePixels,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool assignSubmenuImages)
	{
		var retainedMenus = new List<RetainedMenu>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 20 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, assignSubmenuImages);
			retainedMenus.Add(cycleResult.RetainedMenu);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativeMenus.AddRange(retainedMenus);
		ForceFullGc();

		return ScenarioResult.From(name, retainedMenus, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext context,
		bool assignSubmenuImages)
	{
		var flyout = new MenuFlyout();
		var subitems = new MenuFlyoutSubItem[SubmenusPerMenu];
		var childItems = new MenuFlyoutItem[SubmenusPerMenu];
		var sources = new ImageSource[SubmenusPerMenu];

		for (var submenuIndex = 0; submenuIndex < SubmenusPerMenu; submenuIndex++)
		{
			var imagePath = CreateImageFile(cycle, submenuIndex);
			var source = ImageSource.FromFile(imagePath);
			var subitem = new MenuFlyoutSubItem
			{
				Text = $"Submenu {cycle:000}-{submenuIndex:00}",
				IconImageSource = assignSubmenuImages ? source : null
			};
			var childItem = new MenuFlyoutItem
			{
				Text = $"Child {cycle:000}-{submenuIndex:00}",
				Command = new Command(() => { })
			};

			subitem.Add(childItem);
			flyout.Add(subitem);

			subitems[submenuIndex] = subitem;
			childItems[submenuIndex] = childItem;
			sources[submenuIndex] = source;
		}

		var flyoutHandler = flyout.ToHandler(context);
		var rootMenu = (UIMenu)flyoutHandler.PlatformView!;
		var nativeSubmenus = GetSubmenus(rootMenu);

		if (nativeSubmenus.Count != SubmenusPerMenu)
			throw new InvalidOperationException($"Expected {SubmenusPerMenu} native submenus, found {nativeSubmenus.Count}.");

		var expectedImages = assignSubmenuImages ? SubmenusPerMenu : 0;
		if (CountSubmenusWithImages(rootMenu) != expectedImages)
			throw new InvalidOperationException("Context submenu image assignment did not match the expected count.");

		var subitemHandlers = subitems
			.Select(item => item.Handler ?? throw new InvalidOperationException("Submenu handler was not assigned."))
			.ToArray();
		var childItemHandlers = childItems
			.Select(item => item.Handler ?? throw new InvalidOperationException("Child menu item handler was not assigned."))
			.ToArray();

		var tracked = TrackedCycle.Create(
			cycle,
			rootMenu,
			flyout,
			flyoutHandler,
			subitems,
			childItems,
			sources,
			subitemHandlers,
			childItemHandlers);

		flyoutHandler.DisconnectHandler();
		await DrainMainQueueAsync();

		return new CycleResult(new RetainedMenu(rootMenu), tracked);
	}

	static string CreateImageFile(int cycle, int submenuIndex)
	{
		var path = Path.Combine(AssetDirectory, $"submenu-{cycle:000}-{submenuIndex:00}.png");
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
		});

		using var data = image.AsPNG();
		using var url = NSUrl.FromFilename(path);
		if (data is null || !data.Save(url, false))
			throw new InvalidOperationException($"Failed to write repro image file: {path}");

		return path;
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

	static int CountSubmenusWithImages(UIMenu rootMenu)
	{
		var count = 0;

		foreach (var submenu in GetSubmenus(rootMenu))
		{
			if (submenu.Image is not null)
				count++;
		}

		return count;
	}

	static long EstimateAssignedImageBytes(UIMenu rootMenu)
	{
		long bytes = 0;

		foreach (var submenu in GetSubmenus(rootMenu))
		{
			if (submenu.Image is { } image)
				bytes += EstimateImageBytes(image);
		}

		return bytes;
	}

	static long EstimateImageBytes(UIImage image)
	{
		var width = Math.Max(1, image.CGImage?.Width ?? (int)Math.Ceiling(image.Size.Width * image.CurrentScale));
		var height = Math.Max(1, image.CGImage?.Height ?? (int)Math.Ceiling(image.Size.Height * image.CurrentScale));
		return width * (long)height * 4;
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

	internal sealed record RetainedMenu(UIMenu Menu);

	internal sealed record CycleResult(RetainedMenu RetainedMenu, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<UIMenu> NativeRootMenu,
		WeakReference<MenuFlyout> Flyout,
		WeakReference<IElementHandler> FlyoutHandler,
		WeakReference<MenuFlyoutSubItem>[] Subitems,
		WeakReference<MenuFlyoutItem>[] ChildItems,
		WeakReference<ImageSource>[] Sources,
		WeakReference<IElementHandler>[] SubitemHandlers,
		WeakReference<IElementHandler>[] ChildItemHandlers)
	{
		public static TrackedCycle Create(
			int cycle,
			UIMenu rootMenu,
			MenuFlyout flyout,
			IElementHandler flyoutHandler,
			IReadOnlyList<MenuFlyoutSubItem> subitems,
			IReadOnlyList<MenuFlyoutItem> childItems,
			IReadOnlyList<ImageSource> sources,
			IReadOnlyList<IElementHandler> subitemHandlers,
			IReadOnlyList<IElementHandler> childItemHandlers)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UIMenu>(rootMenu),
				new WeakReference<MenuFlyout>(flyout),
				new WeakReference<IElementHandler>(flyoutHandler),
				subitems.Select(item => new WeakReference<MenuFlyoutSubItem>(item)).ToArray(),
				childItems.Select(item => new WeakReference<MenuFlyoutItem>(item)).ToArray(),
				sources.Select(source => new WeakReference<ImageSource>(source)).ToArray(),
				subitemHandlers.Select(handler => new WeakReference<IElementHandler>(handler)).ToArray(),
				childItemHandlers.Select(handler => new WeakReference<IElementHandler>(handler)).ToArray());
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeRootMenus,
		int RetainedNativeSubmenus,
		int SubmenusWithAssignedImages,
		long EstimatedAssignedImageBytes,
		int AliveNativeRootMenus,
		int AliveFlyouts,
		int AliveFlyoutHandlers,
		int AliveSubitems,
		int AliveChildItems,
		int AliveImageSources,
		int AliveSubitemHandlers,
		int AliveChildItemHandlers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedMenu> retainedMenus,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeSubmenus = 0;
			var submenusWithAssignedImages = 0;
			long estimatedAssignedImageBytes = 0;

			foreach (var retainedMenu in retainedMenus)
			{
				retainedNativeSubmenus += GetSubmenus(retainedMenu.Menu).Count;
				submenusWithAssignedImages += CountSubmenusWithImages(retainedMenu.Menu);
				estimatedAssignedImageBytes += EstimateAssignedImageBytes(retainedMenu.Menu);
			}

			var aliveNativeRootMenus = 0;
			var aliveFlyouts = 0;
			var aliveFlyoutHandlers = 0;
			var aliveSubitems = 0;
			var aliveChildItems = 0;
			var aliveImageSources = 0;
			var aliveSubitemHandlers = 0;
			var aliveChildItemHandlers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeRootMenu.TryGetTarget(out _))
					aliveNativeRootMenus++;

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
				retainedMenus.Count,
				retainedNativeSubmenus,
				submenusWithAssignedImages,
				estimatedAssignedImageBytes,
				aliveNativeRootMenus,
				aliveFlyouts,
				aliveFlyoutHandlers,
				aliveSubitems,
				aliveChildItems,
				aliveImageSources,
				aliveSubitemHandlers,
				aliveChildItemHandlers);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int SubmenusPerMenu,
	int SourceImagePixels,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeRootMenus == Cycles &&
		Control.RetainedNativeSubmenus == ReproSession.TotalSubmenus &&
		Control.SubmenusWithAssignedImages == 0 &&
		Current.RetainedNativeRootMenus == Cycles &&
		Current.RetainedNativeSubmenus == ReproSession.TotalSubmenus &&
		Current.SubmenusWithAssignedImages == ReproSession.TotalSubmenus &&
		Current.EstimatedAssignedImageBytes > Control.EstimatedAssignedImageBytes &&
		Current.AliveFlyouts <= 1 &&
		Current.AliveFlyoutHandlers <= 1 &&
		Current.AliveSubitems <= SubmenusPerMenu &&
		Current.AliveChildItems <= SubmenusPerMenu &&
		Current.AliveImageSources <= SubmenusPerMenu &&
		Current.AliveSubitemHandlers <= SubmenusPerMenu;

	public string ToText()
	{
		var currentMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosContextSubmenuImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Submenus per context menu: {SubmenusPerMenu}",
			$"Source image size: {SourceImagePixels} x {SourceImagePixels} pixels",
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
			$"Control estimated assigned native image payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native image payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeImageMiB = result.EstimatedAssignedImageBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native root menus: {result.RetainedNativeRootMenus}/{result.TrackedCycles}",
			$"  retained native submenus: {result.RetainedNativeSubmenus}/{ReproSession.TotalSubmenus}",
			$"  submenus with assigned images: {result.SubmenusWithAssignedImages}/{ReproSession.TotalSubmenus}",
			$"  estimated assigned native image bytes: {result.EstimatedAssignedImageBytes:N0}",
			$"  estimated assigned native image MiB: {nativeImageMiB:N1}",
			$"  alive native root menus: {result.AliveNativeRootMenus}/{result.TrackedCycles}",
			$"  alive flyouts: {result.AliveFlyouts}/{result.TrackedCycles}",
			$"  alive flyout handlers: {result.AliveFlyoutHandlers}/{result.TrackedCycles}",
			$"  alive subitems: {result.AliveSubitems}/{ReproSession.TotalSubmenus}",
			$"  alive child items: {result.AliveChildItems}/{ReproSession.TotalSubmenus}",
			$"  alive image sources: {result.AliveImageSources}/{ReproSession.TotalSubmenus}",
			$"  alive subitem handlers: {result.AliveSubitemHandlers}/{ReproSession.TotalSubmenus}",
			$"  alive child item handlers: {result.AliveChildItemHandlers}/{ReproSession.TotalSubmenus}");
	}
}
