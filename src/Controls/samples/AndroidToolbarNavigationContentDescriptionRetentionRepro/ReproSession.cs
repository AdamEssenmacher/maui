#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Android.Material.AppBar;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using ControlsToolbar = Microsoft.Maui.Controls.Toolbar;

namespace AndroidToolbarNavigationContentDescriptionRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 1024;
	const int PayloadCharsPerSlot = 16 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * 2;

	static readonly List<MaterialToolbar> RetainedNativeToolbars = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native NavigationContentDescription before disconnect",
			context,
			clearNativeContentDescription: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves native NavigationContentDescription assigned",
			context,
			clearNativeContentDescription: false);

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
		bool clearNativeContentDescription)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(context, i, tracked, clearNativeContentDescription);

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
		bool clearNativeContentDescription)
	{
		var page = new ContentPage
		{
			Title = $"Toolbar page {cycle:D4}"
		};

		var toolbar = new ControlsToolbar(page)
		{
			Title = $"Retained toolbar {cycle:D4}",
			BackButtonVisible = true,
			BackButtonTitle = CreatePayload(cycle),
			IsVisible = true
		};

		var handler = new ToolbarHandler();
		handler.SetMauiContext(context);
		handler.SetVirtualView(toolbar);

		ControlsToolbar.MapBackButtonTitle((IToolbarHandler)handler, toolbar);

		var platformToolbar = handler.PlatformView;

		if (clearNativeContentDescription)
			platformToolbar.NavigationContentDescription = null;

		((IElementHandler)handler).DisconnectHandler();

		RetainedNativeToolbars.Add(platformToolbar);
		tracked.Add(TrackedCycle.Create(cycle, platformToolbar, toolbar, handler));
	}

	static string CreatePayload(int cycle)
	{
		var prefix = $"android-toolbar-navigation-contentdescription-{cycle:D4}-";
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
		WeakReference<ToolbarHandler> Handler)
	{
		public static TrackedCycle Create(
			int cycle,
			MaterialToolbar nativeToolbar,
			object virtualToolbar,
			ToolbarHandler handler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<MaterialToolbar>(nativeToolbar),
				new WeakReference<object>(virtualToolbar),
				new WeakReference<ToolbarHandler>(handler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeToolbars,
		int AliveVirtualToolbars,
		int AliveHandlers,
		int AssignedContentDescriptionSlots,
		int PayloadContentDescriptionSlots,
		long RetainedNativeStringBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeToolbars = 0;
			var aliveVirtualToolbars = 0;
			var aliveHandlers = 0;
			var assignedContentDescriptionSlots = 0;
			var payloadContentDescriptionSlots = 0;
			long retainedNativeStringBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeToolbar.TryGetTarget(out var nativeToolbar))
				{
					aliveNativeToolbars++;
					var contentDescriptionLength = nativeToolbar.NavigationContentDescription?.Length ?? 0;

					if (contentDescriptionLength > 0)
						assignedContentDescriptionSlots++;
					if (contentDescriptionLength >= PayloadCharsPerSlot)
						payloadContentDescriptionSlots++;

					retainedNativeStringBytes += (long)contentDescriptionLength * 2;
				}

				if (cycle.VirtualToolbar.TryGetTarget(out _))
					aliveVirtualToolbars++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeToolbars,
				aliveVirtualToolbars,
				aliveHandlers,
				assignedContentDescriptionSlots,
				payloadContentDescriptionSlots,
				retainedNativeStringBytes);
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
		Control.PayloadContentDescriptionSlots == 0 &&
		Current.PayloadContentDescriptionSlots == Cycles &&
		Current.AliveVirtualToolbars == 0 &&
		Current.AliveHandlers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidToolbarNavigationContentDescriptionRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload chars per native NavigationContentDescription slot: {PayloadCharsPerSlot}",
			$"Payload bytes per native NavigationContentDescription slot: {PayloadBytesPerSlot}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native NavigationContentDescription payload: {FormatBytes(Control.RetainedNativeStringBytes)}",
			$"Current retained native NavigationContentDescription payload: {FormatBytes(Current.RetainedNativeStringBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native toolbars: {result.AliveNativeToolbars}/{result.TrackedCycles}",
			$"  alive virtual toolbars: {result.AliveVirtualToolbars}/{result.TrackedCycles}",
			$"  alive toolbar handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  assigned native NavigationContentDescription slots: {result.AssignedContentDescriptionSlots}/{result.TrackedCycles}",
			$"  payload-sized native NavigationContentDescription slots: {result.PayloadContentDescriptionSlots}/{result.TrackedCycles}",
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
