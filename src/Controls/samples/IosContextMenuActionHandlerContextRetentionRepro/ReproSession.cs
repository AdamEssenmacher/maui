#nullable enable

using System.Runtime.CompilerServices;
using System.Reflection;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace IosContextMenuActionHandlerContextRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 80;
	internal const int ItemsPerMenu = 8;
	internal const int PayloadBytesPerContext = 1024 * 1024;

	static readonly List<RetainedMenu> RetainedNativeMenus = new();
	static readonly FieldInfo MauiContextBackingField =
		typeof(ElementHandler).GetField("<MauiContext>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find ElementHandler.MauiContext backing field.");

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-context-menu-uiaction-handler-context-retention-results.txt");

	public static int TotalActions => Cycles * ItemsPerMenu;

	public static async Task<ReproReport> RunAsync(IMauiContext appContext)
	{
		WriteProgress("Starting iOS context menu UIAction handler context retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear disconnected item handler MauiContext while retaining native actions",
			appContext,
			clearHandlerMauiContextAfterDisconnect: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: retain MAUI-created UIActions that capture disconnected item handlers",
			appContext,
			clearHandlerMauiContextAfterDisconnect: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeMenus);

		return new ReproReport(
			Cycles,
			ItemsPerMenu,
			PayloadBytesPerContext,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext appContext,
		bool clearHandlerMauiContextAfterDisconnect)
	{
		var retainedMenus = new List<RetainedMenu>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 10 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, appContext, clearHandlerMauiContextAfterDisconnect);
			retainedMenus.Add(cycleResult.RetainedMenu);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativeMenus.AddRange(retainedMenus);
		ForceFullGc();

		return ScenarioResult.From(name, retainedMenus, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext appContext,
		bool clearHandlerMauiContextAfterDisconnect)
	{
		var payloadContext = new PayloadMauiContext(appContext, cycle);
		var flyout = new MenuFlyout();
		var items = new MenuFlyoutItem[ItemsPerMenu];

		for (var itemIndex = 0; itemIndex < ItemsPerMenu; itemIndex++)
		{
			var item = new MenuFlyoutItem
			{
				Text = $"Action {cycle:000}-{itemIndex:00}",
				Command = new Command(static () => { })
			};

			flyout.Add(item);
			items[itemIndex] = item;
		}

		var flyoutHandler = flyout.ToHandler(payloadContext);
		var mauiCreatedMenu = (UIMenu)flyoutHandler.PlatformView!;
		var actions = GetActions(mauiCreatedMenu);

		if (actions.Count != ItemsPerMenu)
			throw new InvalidOperationException($"Expected {ItemsPerMenu} UIActions, found {actions.Count}.");

		var itemHandlers = items
			.Select(item => item.Handler ?? throw new InvalidOperationException("Menu item handler was not assigned."))
			.ToArray();

		var tracked = TrackedCycle.Create(
			cycle,
			mauiCreatedMenu,
			payloadContext,
			payloadContext.Payload,
			flyout,
			flyoutHandler,
			items,
			itemHandlers);

		flyoutHandler.DisconnectHandler();

		if (clearHandlerMauiContextAfterDisconnect)
			ClearMauiContext(itemHandlers);

		await DrainMainQueueAsync();

		return new CycleResult(new RetainedMenu(mauiCreatedMenu), tracked);
	}

	static void ClearMauiContext(IEnumerable<IElementHandler> handlers)
	{
		foreach (var handler in handlers)
			MauiContextBackingField.SetValue(handler, null);
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

	internal sealed class PayloadMauiContext : IMauiContext
	{
		readonly IMauiContext _innerContext;

		public PayloadMauiContext(IMauiContext innerContext, int cycle)
		{
			_innerContext = innerContext;
			Payload = new ContextPayload(cycle);
		}

		public IServiceProvider Services => _innerContext.Services;

		public IMauiHandlersFactory Handlers => _innerContext.Handlers;

		public ContextPayload Payload { get; }
	}

	internal sealed class ContextPayload
	{
		readonly byte[] _bytes;

		public ContextPayload(int cycle)
		{
			_bytes = new byte[PayloadBytesPerContext];
			Array.Fill(_bytes, (byte)(cycle % 251));
		}

		public int Length => _bytes.Length;
	}

	internal sealed record RetainedMenu(UIMenu Menu);

	internal sealed record CycleResult(RetainedMenu RetainedMenu, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<UIMenu> NativeMenu,
		WeakReference<PayloadMauiContext> PayloadContext,
		WeakReference<ContextPayload> Payload,
		WeakReference<MenuFlyout> Flyout,
		WeakReference<IElementHandler> FlyoutHandler,
		WeakReference<MenuFlyoutItem>[] Items,
		WeakReference<IElementHandler>[] ItemHandlers)
	{
		public static TrackedCycle Create(
			int cycle,
			UIMenu menu,
			PayloadMauiContext payloadContext,
			ContextPayload payload,
			MenuFlyout flyout,
			IElementHandler flyoutHandler,
			IReadOnlyList<MenuFlyoutItem> items,
			IReadOnlyList<IElementHandler> itemHandlers)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UIMenu>(menu),
				new WeakReference<PayloadMauiContext>(payloadContext),
				new WeakReference<ContextPayload>(payload),
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
		int AliveNativeMenus,
		int AlivePayloadContexts,
		int AlivePayloads,
		long EstimatedAlivePayloadBytes,
		int AliveFlyouts,
		int AliveFlyoutHandlers,
		int AliveMenuItems,
		int AliveItemHandlers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedMenu> retainedMenus,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeActions = 0;
			foreach (var retainedMenu in retainedMenus)
				retainedNativeActions += GetActions(retainedMenu.Menu).Count;

			var aliveNativeMenus = 0;
			var alivePayloadContexts = 0;
			var alivePayloads = 0;
			long estimatedAlivePayloadBytes = 0;
			var aliveFlyouts = 0;
			var aliveFlyoutHandlers = 0;
			var aliveMenuItems = 0;
			var aliveItemHandlers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeMenu.TryGetTarget(out _))
					aliveNativeMenus++;

				if (cycle.PayloadContext.TryGetTarget(out _))
					alivePayloadContexts++;

				if (cycle.Payload.TryGetTarget(out var payload))
				{
					alivePayloads++;
					estimatedAlivePayloadBytes += payload.Length;
				}

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
				retainedMenus.Count,
				retainedNativeActions,
				aliveNativeMenus,
				alivePayloadContexts,
				alivePayloads,
				estimatedAlivePayloadBytes,
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
	int PayloadBytesPerContext,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeMenus == Cycles &&
		Control.RetainedNativeActions == ReproSession.TotalActions &&
		Control.AlivePayloadContexts <= 1 &&
		Control.AlivePayloads <= 1 &&
		Control.AliveItemHandlers >= ReproSession.TotalActions &&
		Current.RetainedNativeMenus == Cycles &&
		Current.RetainedNativeActions == ReproSession.TotalActions &&
		Current.AlivePayloadContexts >= Cycles &&
		Current.AlivePayloads >= Cycles &&
		Current.EstimatedAlivePayloadBytes >= (long)Cycles * PayloadBytesPerContext &&
		Current.AliveItemHandlers >= ReproSession.TotalActions;

	public string ToText()
	{
		var currentPayloadMiB = Current.EstimatedAlivePayloadBytes / 1024d / 1024d;
		var controlPayloadMiB = Control.EstimatedAlivePayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosContextMenuActionHandlerContextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Items per context menu: {ItemsPerMenu}",
			$"Payload per throwaway MauiContext: {PayloadBytesPerContext:N0} bytes",
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
			$"Control estimated retained context payload: {controlPayloadMiB:N1} MiB",
			$"Current estimated retained context payload: {currentPayloadMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var payloadMiB = result.EstimatedAlivePayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native menus: {result.RetainedNativeMenus}/{result.TrackedCycles}",
			$"  retained native actions: {result.RetainedNativeActions}/{ReproSession.TotalActions}",
			$"  alive native menus: {result.AliveNativeMenus}/{result.TrackedCycles}",
			$"  alive payload MauiContexts: {result.AlivePayloadContexts}/{result.TrackedCycles}",
			$"  alive payloads: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  estimated alive payload bytes: {result.EstimatedAlivePayloadBytes:N0}",
			$"  estimated alive payload MiB: {payloadMiB:N1}",
			$"  alive flyouts: {result.AliveFlyouts}/{result.TrackedCycles}",
			$"  alive flyout handlers: {result.AliveFlyoutHandlers}/{result.TrackedCycles}",
			$"  alive menu items: {result.AliveMenuItems}/{ReproSession.TotalActions}",
			$"  alive item handlers: {result.AliveItemHandlers}/{ReproSession.TotalActions}");
	}
}
