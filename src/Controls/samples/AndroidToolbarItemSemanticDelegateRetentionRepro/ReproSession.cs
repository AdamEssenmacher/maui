#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Views;
using AndroidX.Core.View;
using Google.Android.Material.AppBar;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using AView = Android.Views.View;
using ControlsToolbar = Microsoft.Maui.Controls.Toolbar;

namespace AndroidToolbarItemSemanticDelegateRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 1024;
	const int PayloadCharsPerSlot = 16 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * 2;
	const int SemanticSlotsPerCycle = 2;
	const string ToolbarSemanticDelegateTypeName = "Microsoft.Maui.Controls.Platform.ToolbarExtensions+AccessibilityDelegateCompatImpl";

	static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
	static readonly List<MaterialToolbar> RetainedNativeToolbars = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		RetainedNativeToolbars.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native toolbar item semantic accessibility delegates after disconnect",
			context,
			clearNativeDelegates: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves toolbar item semantic accessibility delegates assigned",
			context,
			clearNativeDelegates: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeToolbars);

		return new ReproReport(
			Cycles,
			PayloadCharsPerSlot,
			PayloadBytesPerSlot,
			SemanticSlotsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeDelegates)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(context, i, tracked, clearNativeDelegates);

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
		bool clearNativeDelegates)
	{
		var toolbarItem = new ToolbarItem
		{
			Text = $"Item {cycle:D4}",
			Order = ToolbarItemOrder.Primary
		};

		SemanticProperties.SetDescription(toolbarItem, CreatePayload("description", cycle));
		SemanticProperties.SetHint(toolbarItem, CreatePayload("hint", cycle));

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

		((IElementHandler)handler).DisconnectHandler();

		toolbarItem.ClearValue(SemanticProperties.DescriptionProperty);
		toolbarItem.ClearValue(SemanticProperties.HintProperty);

		if (clearNativeDelegates)
			ClearNativeSemanticDelegates(platformToolbar);

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

	static void ClearNativeSemanticDelegates(MaterialToolbar platformToolbar)
	{
		var menu = platformToolbar.Menu;
		if (menu is null)
			return;

		for (var i = 0; i < menu.Size(); i++)
		{
			var item = menu.GetItem(i);
			if (item is null)
				continue;

			if (platformToolbar.FindViewById(item.ItemId) is AView menuItemView)
				ViewCompat.SetAccessibilityDelegate(menuItemView, null);
		}
	}

	static string CreatePayload(string slot, int cycle)
	{
		var prefix = $"android-toolbaritem-semantic-{slot}-{cycle:D4}-";
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

	static SemanticDelegateSnapshot GetSemanticDelegateSnapshot(MaterialToolbar platformToolbar)
	{
		var menu = platformToolbar.Menu;
		if (menu is null || menu.Size() == 0)
			return SemanticDelegateSnapshot.Empty;

		var item = menu.GetItem(0);
		if (item is null)
			return SemanticDelegateSnapshot.Empty;

		if (platformToolbar.FindViewById(item.ItemId) is not AView menuItemView)
			return SemanticDelegateSnapshot.Empty;

		var accessibilityDelegate = ViewCompat.GetAccessibilityDelegate(menuItemView);
		if (accessibilityDelegate is null)
			return SemanticDelegateSnapshot.Empty;

		var type = accessibilityDelegate.GetType();
		if (type.FullName != ToolbarSemanticDelegateTypeName)
			return new SemanticDelegateSnapshot(true, false, 0, 0);

		var desc = type.GetField("_desc", InstanceNonPublic)?.GetValue(accessibilityDelegate) as string;
		var hint = type.GetField("_hint", InstanceNonPublic)?.GetValue(accessibilityDelegate) as string;

		return new SemanticDelegateSnapshot(
			true,
			true,
			desc?.Length ?? 0,
			hint?.Length ?? 0);
	}

	internal readonly record struct SemanticDelegateSnapshot(
		bool HasAnyAccessibilityDelegate,
		bool HasToolbarSemanticDelegate,
		int DescriptionLength,
		int HintLength)
	{
		public static SemanticDelegateSnapshot Empty => new(false, false, 0, 0);
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
		int AliveMenuItemViews,
		int AssignedAccessibilityDelegates,
		int AssignedToolbarSemanticDelegates,
		int PayloadDescriptionSlots,
		int PayloadHintSlots,
		long RetainedSemanticStringBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeToolbars = 0;
			var aliveVirtualToolbars = 0;
			var aliveToolbarItems = 0;
			var aliveHandlers = 0;
			var aliveMenuItemViews = 0;
			var assignedAccessibilityDelegates = 0;
			var assignedToolbarSemanticDelegates = 0;
			var payloadDescriptionSlots = 0;
			var payloadHintSlots = 0;
			long retainedSemanticStringBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeToolbar.TryGetTarget(out var nativeToolbar))
				{
					aliveNativeToolbars++;
					var snapshot = GetSemanticDelegateSnapshot(nativeToolbar);

					if (snapshot.HasAnyAccessibilityDelegate)
						assignedAccessibilityDelegates++;

					if (snapshot.HasToolbarSemanticDelegate)
					{
						aliveMenuItemViews++;
						assignedToolbarSemanticDelegates++;
					}

					if (snapshot.DescriptionLength >= PayloadCharsPerSlot)
						payloadDescriptionSlots++;

					if (snapshot.HintLength >= PayloadCharsPerSlot)
						payloadHintSlots++;

					retainedSemanticStringBytes += (long)(snapshot.DescriptionLength + snapshot.HintLength) * 2;
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
				aliveMenuItemViews,
				assignedAccessibilityDelegates,
				assignedToolbarSemanticDelegates,
				payloadDescriptionSlots,
				payloadHintSlots,
				retainedSemanticStringBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadCharsPerSlot,
	int PayloadBytesPerSlot,
	int SemanticSlotsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveNativeToolbars == Cycles &&
		Current.AliveNativeToolbars == Cycles &&
		Control.PayloadDescriptionSlots == 0 &&
		Control.PayloadHintSlots == 0 &&
		Current.PayloadDescriptionSlots == Cycles &&
		Current.PayloadHintSlots == Cycles &&
		Current.AliveVirtualToolbars == 0 &&
		Current.AliveToolbarItems == 0 &&
		Current.AliveHandlers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidToolbarItemSemanticDelegateRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload chars per semantic slot: {PayloadCharsPerSlot}",
			$"Payload bytes per semantic slot: {PayloadBytesPerSlot}",
			$"Semantic slots per cycle: {SemanticSlotsPerCycle}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained semantic string payload: {FormatBytes(Control.RetainedSemanticStringBytes)}",
			$"Current retained semantic string payload: {FormatBytes(Current.RetainedSemanticStringBytes)}",
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
			$"  alive native toolbar item views with semantic delegates: {result.AliveMenuItemViews}/{result.TrackedCycles}",
			$"  assigned accessibility delegates: {result.AssignedAccessibilityDelegates}/{result.TrackedCycles}",
			$"  assigned toolbar semantic delegates: {result.AssignedToolbarSemanticDelegates}/{result.TrackedCycles}",
			$"  payload-sized semantic description slots: {result.PayloadDescriptionSlots}/{result.TrackedCycles}",
			$"  payload-sized semantic hint slots: {result.PayloadHintSlots}/{result.TrackedCycles}",
			$"  retained semantic string bytes: {result.RetainedSemanticStringBytes:N0}");
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
