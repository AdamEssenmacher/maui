#nullable enable

using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace IosContextMenuActionImageRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 100;
	internal const int ItemsPerMenu = 8;
	internal const int SourceImagePixels = 192;

	static readonly List<RetainedMenu> RetainedNativeMenus = new();

	static readonly string AssetDirectory =
		Path.Combine(Path.GetTempPath(), "ios-context-menu-uiaction-image-retention-assets-" + Guid.NewGuid().ToString("N"));

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-context-menu-uiaction-image-retention-results.txt");

	public static int TotalActions => Cycles * ItemsPerMenu;

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS context menu action image retention repro.");
		Directory.CreateDirectory(AssetDirectory);
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear UIAction.Image before retaining native context menu",
			context,
			clearActionImages: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: MenuFlyoutItemHandler leaves UIAction.Image assigned",
			context,
			clearActionImages: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeMenus);

		return new ReproReport(
			Cycles,
			ItemsPerMenu,
			SourceImagePixels,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearActionImages)
	{
		var retainedMenus = new List<RetainedMenu>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 20 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, clearActionImages);
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
		bool clearActionImages)
	{
		var flyout = new MenuFlyout();
		var items = new MenuFlyoutItem[ItemsPerMenu];
		var sources = new ImageSource[ItemsPerMenu];

		for (var itemIndex = 0; itemIndex < ItemsPerMenu; itemIndex++)
		{
			var imagePath = CreateImageFile(cycle, itemIndex);
			var source = ImageSource.FromFile(imagePath);
			var item = new MenuFlyoutItem
			{
				Text = $"Action {cycle:000}-{itemIndex:00}",
				IconImageSource = source,
				Command = new Command(() => { })
			};

			flyout.Add(item);
			items[itemIndex] = item;
			sources[itemIndex] = source;
		}

		var flyoutHandler = flyout.ToHandler(context);
		var menu = (UIMenu)flyoutHandler.PlatformView!;
		var actions = GetActions(menu);

		if (actions.Count != ItemsPerMenu)
			throw new InvalidOperationException($"Expected {ItemsPerMenu} UIActions, found {actions.Count}.");

		if (CountActionsWithImages(menu) != ItemsPerMenu)
			throw new InvalidOperationException("Context menu action image assignment did not populate every expected UIAction.");

		var itemHandlers = items
			.Select(item => item.Handler ?? throw new InvalidOperationException("Menu item handler was not assigned."))
			.ToArray();

		var tracked = TrackedCycle.Create(cycle, menu, flyout, flyoutHandler, items, sources, itemHandlers);

		if (clearActionImages)
			ClearActionImages(menu);

		flyoutHandler.DisconnectHandler();
		await DrainMainQueueAsync();

		return new CycleResult(new RetainedMenu(menu), tracked);
	}

	static string CreateImageFile(int cycle, int itemIndex)
	{
		var path = Path.Combine(AssetDirectory, $"icon-{cycle:000}-{itemIndex:00}.png");
		var format = new UIGraphicsImageRendererFormat
		{
			Opaque = true,
			Scale = 1
		};
		var renderer = new UIGraphicsImageRenderer(new CGSize(SourceImagePixels, SourceImagePixels), format);

		using var image = renderer.CreateImage(context =>
		{
			UIColor.FromRGB(
				(nfloat)((cycle * 37 + itemIndex * 41) % 255) / 255f,
				(nfloat)((cycle * 79 + itemIndex * 67) % 255) / 255f,
				(nfloat)((cycle * 113 + itemIndex * 97) % 255) / 255f).SetFill();
			context.FillRect(new CGRect(0, 0, SourceImagePixels, SourceImagePixels));

			UIColor.White.SetStroke();
			context.CGContext.SetLineWidth(4);
			context.CGContext.StrokeEllipseInRect(new CGRect(20 + itemIndex, 20 + itemIndex, SourceImagePixels - 40, SourceImagePixels - 40));
		});

		using var data = image.AsPNG();
		using var url = NSUrl.FromFilename(path);
		if (data is null || !data.Save(url, false))
			throw new InvalidOperationException($"Failed to write repro image file: {path}");

		return path;
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

	static void ClearActionImages(UIMenu menu)
	{
		foreach (var action in GetActions(menu))
			action.Image = null;
	}

	static int CountActionsWithImages(UIMenu menu)
	{
		var count = 0;

		foreach (var action in GetActions(menu))
		{
			if (action.Image is not null)
				count++;
		}

		return count;
	}

	static long EstimateAssignedImageBytes(UIMenu menu)
	{
		long bytes = 0;

		foreach (var action in GetActions(menu))
		{
			if (action.Image is { } image)
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
		WeakReference<UIMenu> NativeMenu,
		WeakReference<MenuFlyout> Flyout,
		WeakReference<IElementHandler> FlyoutHandler,
		WeakReference<MenuFlyoutItem>[] Items,
		WeakReference<ImageSource>[] Sources,
		WeakReference<IElementHandler>[] ItemHandlers)
	{
		public static TrackedCycle Create(
			int cycle,
			UIMenu menu,
			MenuFlyout flyout,
			IElementHandler flyoutHandler,
			IReadOnlyList<MenuFlyoutItem> items,
			IReadOnlyList<ImageSource> sources,
			IReadOnlyList<IElementHandler> itemHandlers)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UIMenu>(menu),
				new WeakReference<MenuFlyout>(flyout),
				new WeakReference<IElementHandler>(flyoutHandler),
				items.Select(item => new WeakReference<MenuFlyoutItem>(item)).ToArray(),
				sources.Select(source => new WeakReference<ImageSource>(source)).ToArray(),
				itemHandlers.Select(handler => new WeakReference<IElementHandler>(handler)).ToArray());
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeMenus,
		int RetainedNativeActions,
		int ActionsWithAssignedImages,
		long EstimatedAssignedImageBytes,
		int AliveNativeMenus,
		int AliveFlyouts,
		int AliveFlyoutHandlers,
		int AliveMenuItems,
		int AliveImageSources,
		int AliveItemHandlers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedMenu> retainedMenus,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeActions = 0;
			var actionsWithAssignedImages = 0;
			long estimatedAssignedImageBytes = 0;

			foreach (var retainedMenu in retainedMenus)
			{
				retainedNativeActions += GetActions(retainedMenu.Menu).Count;
				actionsWithAssignedImages += CountActionsWithImages(retainedMenu.Menu);
				estimatedAssignedImageBytes += EstimateAssignedImageBytes(retainedMenu.Menu);
			}

			var aliveNativeMenus = 0;
			var aliveFlyouts = 0;
			var aliveFlyoutHandlers = 0;
			var aliveMenuItems = 0;
			var aliveImageSources = 0;
			var aliveItemHandlers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeMenu.TryGetTarget(out _))
					aliveNativeMenus++;

				if (cycle.Flyout.TryGetTarget(out _))
					aliveFlyouts++;

				if (cycle.FlyoutHandler.TryGetTarget(out _))
					aliveFlyoutHandlers++;

				foreach (var item in cycle.Items)
				{
					if (item.TryGetTarget(out _))
						aliveMenuItems++;
				}

				foreach (var source in cycle.Sources)
				{
					if (source.TryGetTarget(out _))
						aliveImageSources++;
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
				retainedMenus.Count,
				retainedNativeActions,
				actionsWithAssignedImages,
				estimatedAssignedImageBytes,
				aliveNativeMenus,
				aliveFlyouts,
				aliveFlyoutHandlers,
				aliveMenuItems,
				aliveImageSources,
				aliveItemHandlers);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int ItemsPerMenu,
	int SourceImagePixels,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeMenus == Cycles &&
		Control.RetainedNativeActions == ReproSession.TotalActions &&
		Control.ActionsWithAssignedImages == 0 &&
		Current.RetainedNativeMenus == Cycles &&
		Current.RetainedNativeActions == ReproSession.TotalActions &&
		Current.ActionsWithAssignedImages == ReproSession.TotalActions &&
		Current.EstimatedAssignedImageBytes > Control.EstimatedAssignedImageBytes &&
		Current.AliveFlyouts <= 1 &&
		Current.AliveFlyoutHandlers <= 1 &&
		Current.AliveMenuItems <= ItemsPerMenu &&
		Current.AliveImageSources <= ItemsPerMenu;

	public string ToText()
	{
		var currentMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosContextMenuActionImageRetentionRepro",
			$"Cycles: {Cycles}",
			$"Items per context menu: {ItemsPerMenu}",
			$"Source image size: {SourceImagePixels} x {SourceImagePixels} pixels",
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
			$"  retained native menus: {result.RetainedNativeMenus}/{result.TrackedCycles}",
			$"  retained native actions: {result.RetainedNativeActions}/{ReproSession.TotalActions}",
			$"  actions with assigned images: {result.ActionsWithAssignedImages}/{ReproSession.TotalActions}",
			$"  estimated assigned native image bytes: {result.EstimatedAssignedImageBytes:N0}",
			$"  estimated assigned native image MiB: {nativeImageMiB:N1}",
			$"  alive native menus: {result.AliveNativeMenus}/{result.TrackedCycles}",
			$"  alive flyouts: {result.AliveFlyouts}/{result.TrackedCycles}",
			$"  alive flyout handlers: {result.AliveFlyoutHandlers}/{result.TrackedCycles}",
			$"  alive menu items: {result.AliveMenuItems}/{ReproSession.TotalActions}",
			$"  alive image sources: {result.AliveImageSources}/{ReproSession.TotalActions}",
			$"  alive item handlers: {result.AliveItemHandlers}/{ReproSession.TotalActions}");
	}
}
