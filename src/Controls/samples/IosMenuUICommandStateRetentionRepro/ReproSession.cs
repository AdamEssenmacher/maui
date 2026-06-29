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
using Microsoft.Maui.Platform;
using ObjCRuntime;
using UIKit;

namespace IosMenuUICommandStateRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 128;
	internal const int ItemsPerCycle = 8;
	internal const int PayloadKiBPerTitle = 8;
	internal const int SourceImagePixels = 192;

	const long PayloadBytesPerTitle = PayloadKiBPerTitle * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedNativeCommand>> RetainedNativeCommands = new();

	static readonly string AssetDirectory =
		Path.Combine(Path.GetTempPath(), "ios-menu-uicommand-state-retention-assets-" + Guid.NewGuid().ToString("N"));

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-menu-uicommand-state-retention-results.txt");

	public static int TotalCommands => Cycles * ItemsPerCycle;

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS non-context menu UICommand state retention repro.");
		Directory.CreateDirectory(AssetDirectory);
		StaticMenuStore.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running short-value control scenario.");
		var control = await RunScenarioAsync(
			"control: retained native UICommands with short titles and no images",
			context,
			usePayloadState: false);

		WriteProgress("Running current MAUI payload scenario.");
		var current = await RunScenarioAsync(
			"current: MenuFlyoutItemHandler leaves UICommand title/image state assigned",
			context,
			usePayloadState: true);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeCommands);

		return new ReproReport(
			Cycles,
			ItemsPerCycle,
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
		var retainedCommands = new List<RetainedNativeCommand>(TotalCommands);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, context, usePayloadState);
			retainedCommands.AddRange(cycleResult.RetainedCommands);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativeCommands.Add(retainedCommands);
		ForceFullGc();

		return ScenarioResult.From(name, retainedCommands, tracked, StaticMenuStore.Count);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext context,
		bool usePayloadState)
	{
		var retainedCommands = new List<RetainedNativeCommand>(ItemsPerCycle);
		var items = new MenuFlyoutItem[ItemsPerCycle];
		var sources = new ImageSource?[ItemsPerCycle];
		var handlers = new IElementHandler[ItemsPerCycle];
		var nativeCommands = new UICommand[ItemsPerCycle];

		try
		{
			for (var itemIndex = 0; itemIndex < ItemsPerCycle; itemIndex++)
			{
				var item = CreateMenuItem(cycle, itemIndex, usePayloadState, out var source);
				var handler = new MenuFlyoutItemHandler();
				handler.SetMauiContext(context);
				handler.SetVirtualView(item);

				if (handler.PlatformView is not UICommand command)
					throw new InvalidOperationException($"Expected a non-context UICommand, found {handler.PlatformView?.GetType().FullName ?? "null"}.");

				items[itemIndex] = item;
				sources[itemIndex] = source;
				handlers[itemIndex] = handler;
				nativeCommands[itemIndex] = command;
				retainedCommands.Add(RetainNativeCommand(command));
			}

			if (StaticMenuStore.Count != ItemsPerCycle)
				throw new InvalidOperationException($"Expected {ItemsPerCycle} static menu entries before reset, found {StaticMenuStore.Count}.");

			if (usePayloadState)
			{
				if (CountCommandsWithPayloadTitles(nativeCommands) != ItemsPerCycle)
					throw new InvalidOperationException("Payload title assignment did not populate every expected native menu command.");

				if (CountCommandsWithImages(nativeCommands) != ItemsPerCycle)
					throw new InvalidOperationException("Payload image assignment did not populate every expected native menu command.");
			}
			else
			{
				if (CountCommandsWithPayloadTitles(nativeCommands) != 0)
					throw new InvalidOperationException("Short-title control unexpectedly created payload-sized native command titles.");

				if (CountCommandsWithImages(nativeCommands) != 0)
					throw new InvalidOperationException("No-image control unexpectedly created native command images.");
			}

			var tracked = TrackedCycle.Create(cycle, items, sources, handlers);

			foreach (var handler in handlers)
				handler.DisconnectHandler();

			StaticMenuStore.Clear();
			await DrainMainQueueAsync();

			return new CycleResult(retainedCommands, tracked);
		}
		catch
		{
			StaticMenuStore.Clear();
			throw;
		}
	}

	static MenuFlyoutItem CreateMenuItem(
		int cycle,
		int itemIndex,
		bool usePayloadState,
		out ImageSource? source)
	{
		var item = new MenuFlyoutItem
		{
			Text = usePayloadState
				? CreatePayloadTitle(cycle, itemIndex)
				: $"Action {cycle:000}-{itemIndex:00}"
		};

		if (usePayloadState)
		{
			var imagePath = CreateImageFile(cycle, itemIndex);
			source = ImageSource.FromFile(imagePath);
			item.IconImageSource = source;
		}
		else
		{
			source = null;
		}

		if ((itemIndex % 2) == 1)
		{
			item.KeyboardAccelerators.Add(new KeyboardAccelerator
			{
				Key = ((char)('A' + itemIndex)).ToString(),
				Modifiers = KeyboardAcceleratorModifiers.Cmd
			});
		}

		return item;
	}

	static string CreatePayloadTitle(int cycle, int itemIndex)
	{
		var header = $"Cycle {cycle:0000} menu command {itemIndex:00} offline operations action. ";
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

	static string CreateImageFile(int cycle, int itemIndex)
	{
		var path = Path.Combine(AssetDirectory, $"menu-command-{cycle:000}-{itemIndex:00}.png");
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
			context.CGContext.MoveTo(36, SourceImagePixels - 36 - itemIndex);
			context.CGContext.AddLineToPoint(SourceImagePixels - 36, 36 + itemIndex);
			context.CGContext.StrokePath();
		});

		using var data = image.AsPNG();
		using var url = NSUrl.FromFilename(path);
		if (data is null || !data.Save(url, false))
			throw new InvalidOperationException($"Failed to write repro image file: {path}");

		return path;
	}

	static int CountCommandsWithPayloadTitles(IEnumerable<UICommand> commands)
	{
		var count = 0;

		foreach (var command in commands)
		{
			if (EstimateTitleBytes(command) >= PayloadBytesPerTitle * 0.95)
				count++;
		}

		return count;
	}

	static int CountCommandsWithImages(IEnumerable<UICommand> commands)
	{
		var count = 0;

		foreach (var command in commands)
		{
			if (command.Image is not null)
				count++;
		}

		return count;
	}

	static long EstimateAssignedTitleBytes(IEnumerable<RetainedNativeCommand> retainedCommands)
	{
		long bytes = 0;

		foreach (var retainedCommand in retainedCommands)
		{
			if (retainedCommand.TryGetCommand() is { } command)
				bytes += Math.Min(EstimateTitleBytes(command), PayloadBytesPerTitle);
		}

		return bytes;
	}

	static long EstimateAssignedImageBytes(IEnumerable<RetainedNativeCommand> retainedCommands)
	{
		long bytes = 0;

		foreach (var retainedCommand in retainedCommands)
		{
			if (retainedCommand.TryGetCommand()?.Image is { } image)
				bytes += EstimateImageBytes(image);
		}

		return bytes;
	}

	static long EstimateTitleBytes(UICommand command)
	{
		return string.IsNullOrEmpty(command.Title) ? 0 : command.Title.Length * 2L;
	}

	static long EstimateImageBytes(UIImage image)
	{
		var width = Math.Max(1, image.CGImage?.Width ?? (int)Math.Ceiling(image.Size.Width * image.CurrentScale));
		var height = Math.Max(1, image.CGImage?.Height ?? (int)Math.Ceiling(image.Size.Height * image.CurrentScale));
		return width * (long)height * 4;
	}

	static RetainedNativeCommand RetainNativeCommand(UICommand command)
	{
		var handle = command.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UICommand with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedNativeCommand(retained);
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

	internal sealed record CycleResult(IReadOnlyList<RetainedNativeCommand> RetainedCommands, TrackedCycle Tracked);

	internal sealed class RetainedNativeCommand
	{
		public RetainedNativeCommand(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UICommand? TryGetCommand()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UICommand>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<MenuFlyoutItem>[] Items,
		WeakReference<ImageSource>[] Sources,
		WeakReference<IElementHandler>[] ItemHandlers)
	{
		public static TrackedCycle Create(
			int cycle,
			IReadOnlyList<MenuFlyoutItem> items,
			IReadOnlyList<ImageSource?> sources,
			IReadOnlyList<IElementHandler> itemHandlers)
		{
			return new TrackedCycle(
				cycle,
				items.Select(item => new WeakReference<MenuFlyoutItem>(item)).ToArray(),
				sources.Where(source => source is not null).Select(source => new WeakReference<ImageSource>(source!)).ToArray(),
				itemHandlers.Select(handler => new WeakReference<IElementHandler>(handler)).ToArray());
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeCommands,
		int RetainedKeyCommands,
		int CommandsWithPayloadTitles,
		int CommandsWithImages,
		long EstimatedAssignedTitleBytes,
		long EstimatedAssignedImageBytes,
		int AliveMenuItems,
		int AliveImageSources,
		int AliveItemHandlers,
		int StaticMenuCountAfterScenario)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedNativeCommand> retainedCommands,
			IReadOnlyList<TrackedCycle> tracked,
			int staticMenuCountAfterScenario)
		{
			var retainedNativeCommands = 0;
			var retainedKeyCommands = 0;
			var commandsWithPayloadTitles = 0;
			var commandsWithImages = 0;

			foreach (var retainedCommand in retainedCommands)
			{
				var command = retainedCommand.TryGetCommand();
				if (command is null)
					continue;

				retainedNativeCommands++;
				if (command is UIKeyCommand)
					retainedKeyCommands++;

				if (EstimateTitleBytes(command) >= PayloadBytesPerTitle * 0.95)
					commandsWithPayloadTitles++;

				if (command.Image is not null)
					commandsWithImages++;
			}

			var aliveMenuItems = 0;
			var aliveImageSources = 0;
			var aliveItemHandlers = 0;

			foreach (var cycle in tracked)
			{
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
				retainedNativeCommands,
				retainedKeyCommands,
				commandsWithPayloadTitles,
				commandsWithImages,
				EstimateAssignedTitleBytes(retainedCommands),
				EstimateAssignedImageBytes(retainedCommands),
				aliveMenuItems,
				aliveImageSources,
				aliveItemHandlers,
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
	int ItemsPerCycle,
	int PayloadKiBPerTitle,
	int SourceImagePixels,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeCommands == ReproSession.TotalCommands &&
		Control.RetainedKeyCommands == ReproSession.TotalCommands / 2 &&
		Control.CommandsWithPayloadTitles == 0 &&
		Control.CommandsWithImages == 0 &&
		Control.StaticMenuCountAfterScenario == 0 &&
		Current.RetainedNativeCommands == ReproSession.TotalCommands &&
		Current.RetainedKeyCommands == ReproSession.TotalCommands / 2 &&
		Current.CommandsWithPayloadTitles == ReproSession.TotalCommands &&
		Current.CommandsWithImages == ReproSession.TotalCommands &&
		Current.EstimatedAssignedTitleBytes >= ReproSession.TotalCommands * PayloadKiBPerTitle * 1024L * 0.95 &&
		Current.EstimatedAssignedImageBytes > Control.EstimatedAssignedImageBytes &&
		Current.AliveMenuItems <= ItemsPerCycle &&
		Current.AliveImageSources <= ItemsPerCycle &&
		Current.AliveItemHandlers <= ItemsPerCycle &&
		Current.StaticMenuCountAfterScenario == 0;

	public string ToText()
	{
		var currentTitleMiB = Current.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var controlTitleMiB = Control.EstimatedAssignedTitleBytes / 1024d / 1024d;
		var currentImageMiB = Current.EstimatedAssignedImageBytes / 1024d / 1024d;
		var controlImageMiB = Control.EstimatedAssignedImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosMenuUICommandStateRetentionRepro",
			$"Cycles: {Cycles}",
			$"Native menu commands per cycle: {ItemsPerCycle}",
			$"Payload per command title: {PayloadKiBPerTitle} KiB",
			$"Source image size: {SourceImagePixels} x {SourceImagePixels} pixels",
			$"Total retained native commands: {ReproSession.TotalCommands}",
			"Note: the static MenuFlyoutItemHandler.menus dictionary is cleared after every cycle, so C026 is not part of this proof.",
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
			$"  retained native commands: {result.RetainedNativeCommands}/{ReproSession.TotalCommands}",
			$"  retained native key commands: {result.RetainedKeyCommands}/{ReproSession.TotalCommands / 2}",
			$"  commands with payload-sized titles: {result.CommandsWithPayloadTitles}/{ReproSession.TotalCommands}",
			$"  commands with assigned images: {result.CommandsWithImages}/{ReproSession.TotalCommands}",
			$"  estimated retained native title bytes: {result.EstimatedAssignedTitleBytes:N0}",
			$"  estimated retained native title MiB: {nativeTitleMiB:N1}",
			$"  estimated retained native image bytes: {result.EstimatedAssignedImageBytes:N0}",
			$"  estimated retained native image MiB: {nativeImageMiB:N1}",
			$"  alive menu items: {result.AliveMenuItems}/{ReproSession.TotalCommands}",
			$"  alive image sources: {result.AliveImageSources}/{ReproSession.TotalCommands}",
			$"  alive item handlers: {result.AliveItemHandlers}/{ReproSession.TotalCommands}",
			$"  static menu dictionary count after scenario: {result.StaticMenuCountAfterScenario}");
	}
}
