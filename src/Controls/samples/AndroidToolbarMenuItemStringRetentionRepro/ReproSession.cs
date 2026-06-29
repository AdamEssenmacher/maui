#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Views;
using AndroidX.Core.View;
using Google.Android.Material.AppBar;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using ControlsToolbar = Microsoft.Maui.Controls.Toolbar;

namespace AndroidToolbarMenuItemStringRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 1024;
	const int PayloadCharsPerSlot = 16 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * 2;
	const int NativeStringSlotsPerCycle = 2;

	static readonly List<MaterialToolbar> RetainedNativeToolbars = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native title/content-description slots before disconnect",
			context,
			clearNativeStrings: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves native title/content-description slots assigned",
			context,
			clearNativeStrings: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeToolbars);

		return new ReproReport(
			Cycles,
			PayloadCharsPerSlot,
			PayloadBytesPerSlot,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeStrings)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(context, i, tracked, clearNativeStrings);

			if (i % 64 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeStrings)
	{
		var toolbarItem = new ToolbarItem
		{
			Text = CreatePayload("title", cycle),
			AutomationId = CreatePayload("contentdescription", cycle),
			Order = ToolbarItemOrder.Primary
		};

		var page = new ContentPage
		{
			Title = $"Toolbar page {cycle:D4}"
		};

		var toolbar = new ControlsToolbar(page)
		{
			Title = $"Retained toolbar {cycle:D4}",
			BackButtonVisible = false,
			IsVisible = true,
			ToolbarItems = new[] { toolbarItem }
		};

		var handler = new ToolbarHandler();
		handler.SetMauiContext(context);
		handler.SetVirtualView(toolbar);

		ControlsToolbar.MapToolbarItems((IToolbarHandler)handler, toolbar);

		var platformToolbar = handler.PlatformView;
		ClearNativeClickListeners(platformToolbar);

		if (clearNativeStrings)
			ClearNativeStringSlots(platformToolbar);

		((IElementHandler)handler).DisconnectHandler();

		RetainedNativeToolbars.Add(platformToolbar);
		tracked.Add(TrackedCycle.Create(cycle, platformToolbar, toolbar, toolbarItem, handler));
	}

	static void ClearNativeClickListeners(MaterialToolbar platformToolbar)
	{
		var menu = platformToolbar.Menu;
		if (menu is null)
			return;

		for (var i = 0; i < menu.Size(); i++)
			menu.GetItem(i)?.SetOnMenuItemClickListener(null);
	}

	static void ClearNativeStringSlots(MaterialToolbar platformToolbar)
	{
		var menu = platformToolbar.Menu;
		if (menu is null)
			return;

		for (var i = 0; i < menu.Size(); i++)
		{
			var item = menu.GetItem(i);
			if (item is null)
				continue;

			item.SetTitle(string.Empty);
			MenuItemCompat.SetContentDescription(item, (Java.Lang.ICharSequence?)null);
		}
	}

	static string CreatePayload(string slot, int cycle)
	{
		var prefix = $"android-toolbar-menuitem-{slot}-{cycle:D4}-";
		return prefix + new string((char)('A' + (cycle % 26)), PayloadCharsPerSlot - prefix.Length);
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<MaterialToolbar> NativeToolbar,
		WeakReference<object> VirtualToolbar,
		WeakReference<ToolbarItem> ToolbarItem,
		WeakReference<ToolbarHandler> Handler)
	{
		public static TrackedCycle Create(
			int cycle,
			MaterialToolbar nativeToolbar,
			object virtualToolbar,
			ToolbarItem toolbarItem,
			ToolbarHandler handler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<MaterialToolbar>(nativeToolbar),
				new WeakReference<object>(virtualToolbar),
				new WeakReference<ToolbarItem>(toolbarItem),
				new WeakReference<ToolbarHandler>(handler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeToolbars,
		int AliveVirtualToolbars,
		int AliveToolbarItems,
		int AliveHandlers,
		int AliveMenuItems,
		int AssignedTitleSlots,
		int PayloadTitleSlots,
		int AssignedContentDescriptionSlots,
		int PayloadContentDescriptionSlots,
		long RetainedNativeStringBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeToolbars = 0;
			var aliveVirtualToolbars = 0;
			var aliveToolbarItems = 0;
			var aliveHandlers = 0;
			var aliveMenuItems = 0;
			var assignedTitleSlots = 0;
			var payloadTitleSlots = 0;
			var assignedContentDescriptionSlots = 0;
			var payloadContentDescriptionSlots = 0;
			long retainedNativeStringBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeToolbar.TryGetTarget(out var nativeToolbar))
				{
					aliveNativeToolbars++;
					var menu = nativeToolbar.Menu;
					var menuSize = menu?.Size() ?? 0;

					if (menuSize > 0)
					{
						aliveMenuItems++;
						var item = menu!.GetItem(0);
						var titleLength = GetTitleLength(item);
						var contentDescriptionLength = GetContentDescriptionLength(item);

						if (titleLength > 0)
							assignedTitleSlots++;
						if (titleLength >= PayloadCharsPerSlot)
							payloadTitleSlots++;

						if (contentDescriptionLength > 0)
							assignedContentDescriptionSlots++;
						if (contentDescriptionLength >= PayloadCharsPerSlot)
							payloadContentDescriptionSlots++;

						retainedNativeStringBytes += (long)(titleLength + contentDescriptionLength) * 2;
					}
				}

				if (cycle.VirtualToolbar.TryGetTarget(out _))
					aliveVirtualToolbars++;

				if (cycle.ToolbarItem.TryGetTarget(out _))
					aliveToolbarItems++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeToolbars,
				aliveVirtualToolbars,
				aliveToolbarItems,
				aliveHandlers,
				aliveMenuItems,
				assignedTitleSlots,
				payloadTitleSlots,
				assignedContentDescriptionSlots,
				payloadContentDescriptionSlots,
				retainedNativeStringBytes);
		}

		static int GetTitleLength(IMenuItem? item)
		{
			if (item is null)
				return 0;

			var title = item.TitleFormatted;
			return title?.Length() ?? 0;
		}

		static int GetContentDescriptionLength(IMenuItem? item)
		{
			if (item is null)
				return 0;

			var contentDescription = MenuItemCompat.GetContentDescription(item);
			return contentDescription?.Length ?? 0;
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadCharsPerSlot,
	int PayloadBytesPerSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveNativeToolbars == Cycles &&
		Current.AliveNativeToolbars == Cycles &&
		Control.PayloadTitleSlots == 0 &&
		Control.PayloadContentDescriptionSlots == 0 &&
		Current.PayloadTitleSlots == Cycles &&
		Current.PayloadContentDescriptionSlots == Cycles &&
		Current.AliveVirtualToolbars == 0 &&
		Current.AliveToolbarItems == 0 &&
		Current.AliveHandlers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidToolbarMenuItemStringRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload chars per native string slot: {PayloadCharsPerSlot}",
			$"Payload bytes per native string slot: {PayloadBytesPerSlot}",
			$"Native string slots per cycle: {2}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native string payload: {FormatBytes(Control.RetainedNativeStringBytes)}",
			$"Current retained native string payload: {FormatBytes(Current.RetainedNativeStringBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native toolbars: {result.AliveNativeToolbars}/{result.TrackedCycles}",
			$"  alive virtual toolbars: {result.AliveVirtualToolbars}/{result.TrackedCycles}",
			$"  alive toolbar items: {result.AliveToolbarItems}/{result.TrackedCycles}",
			$"  alive toolbar handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive native menu items: {result.AliveMenuItems}/{result.TrackedCycles}",
			$"  assigned native title slots: {result.AssignedTitleSlots}/{result.TrackedCycles}",
			$"  payload-sized native title slots: {result.PayloadTitleSlots}/{result.TrackedCycles}",
			$"  assigned native ContentDescription slots: {result.AssignedContentDescriptionSlots}/{result.TrackedCycles}",
			$"  payload-sized native ContentDescription slots: {result.PayloadContentDescriptionSlots}/{result.TrackedCycles}",
			$"  retained native string bytes: {result.RetainedNativeStringBytes:N0}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024d / 1024d:N1} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024d:N1} KiB";
		return $"{bytes:N0} B";
	}
}
