#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.Widget;
using Google.Android.Material.Button;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using AView = Android.Views.View;
using MauiButton = Microsoft.Maui.Controls.Button;
using MauiEditor = Microsoft.Maui.Controls.Editor;
using MauiEntry = Microsoft.Maui.Controls.Entry;
using MauiLabel = Microsoft.Maui.Controls.Label;
using MauiSearchBar = Microsoft.Maui.Controls.SearchBar;
using SearchView = AndroidX.AppCompat.Widget.SearchView;

namespace AndroidNativeTextSlotRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 40;
	const int PayloadCharsPerSlot = 128 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * sizeof(char);

	static readonly List<AView> RetainedNativePeers = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native text and hint slots before disconnect",
			context,
			clearNativeText: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves native text and hint slots assigned",
			context,
			clearNativeText: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

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
		bool clearNativeText)
	{
		var tracked = new List<TrackedCycle>(Cycles * 5);

		for (var i = 0; i < Cycles; i++)
		{
			CreateLabelCycle(context, i, tracked, clearNativeText);
			CreateButtonCycle(context, i, tracked, clearNativeText);
			CreateEntryCycle(context, i, tracked, clearNativeText);
			CreateEditorCycle(context, i, tracked, clearNativeText);
			CreateSearchBarCycle(context, i, tracked, clearNativeText);
		}

		await Task.Delay(250);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateLabelCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var text = CreatePayload("Label", cycle, "Text");
		var label = new MauiLabel { Text = text };
		var handler = new LabelHandler();

		AttachHandler(label, handler, context);
		LabelHandler.MapText(handler, label);

		var platformView = handler.PlatformView;
		if (clearNativeText)
			ClearTextView(platformView);

		Disconnect(label, handler);
		label.Text = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("Label", cycle, platformView, label, handler, expectedPayloadSlots: 1));
	}

	static void CreateButtonCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var text = CreatePayload("Button", cycle, "Text");
		var button = new MauiButton { Text = text };
		var handler = new ButtonHandler();

		AttachHandler(button, handler, context);
		ButtonHandler.MapText(handler, button);

		var platformView = handler.PlatformView;
		if (clearNativeText)
			ClearTextView(platformView);

		Disconnect(button, handler);
		button.Text = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("Button", cycle, platformView, button, handler, expectedPayloadSlots: 1));
	}

	static void CreateEntryCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var text = CreatePayload("Entry", cycle, "Text");
		var placeholder = CreatePayload("Entry", cycle, "Placeholder");
		var entry = new MauiEntry
		{
			Text = text,
			Placeholder = placeholder
		};
		var handler = new EntryHandler();

		AttachHandler(entry, handler, context);
		EntryHandler.MapText(handler, entry);
		EntryHandler.MapPlaceholder(handler, entry);

		var platformView = handler.PlatformView;
		if (clearNativeText)
			ClearEditText(platformView);

		Disconnect(entry, handler);
		entry.Text = null;
		entry.Placeholder = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("Entry", cycle, platformView, entry, handler, expectedPayloadSlots: 2));
	}

	static void CreateEditorCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var text = CreatePayload("Editor", cycle, "Text");
		var placeholder = CreatePayload("Editor", cycle, "Placeholder");
		var editor = new MauiEditor
		{
			Text = text,
			Placeholder = placeholder
		};
		var handler = new EditorHandler();

		AttachHandler(editor, handler, context);
		EditorHandler.MapText(handler, editor);
		EditorHandler.MapPlaceholder(handler, editor);

		var platformView = handler.PlatformView;
		if (clearNativeText)
			ClearEditText(platformView);

		Disconnect(editor, handler);
		editor.Text = null;
		editor.Placeholder = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("Editor", cycle, platformView, editor, handler, expectedPayloadSlots: 2));
	}

	static void CreateSearchBarCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var text = CreatePayload("SearchBar", cycle, "Text");
		var placeholder = CreatePayload("SearchBar", cycle, "Placeholder");
		var searchBar = new MauiSearchBar
		{
			Text = text,
			Placeholder = placeholder
		};
		var handler = new SearchBarHandler();

		AttachHandler(searchBar, handler, context);
		SearchBarHandler.MapText(handler, searchBar);
		SearchBarHandler.MapPlaceholder(handler, searchBar);

		var platformView = handler.PlatformView;
		if (clearNativeText)
			ClearSearchView(platformView);

		Disconnect(searchBar, handler);
		searchBar.Text = null;
		searchBar.Placeholder = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("SearchBar", cycle, platformView, searchBar, handler, expectedPayloadSlots: 2));
	}

	static void AttachHandler(IElement view, IElementHandler handler, IMauiContext context)
	{
		handler.SetMauiContext(context);
		view.Handler = handler;
	}

	static void Disconnect(IElement view, IElementHandler handler)
	{
		((IElementHandler)handler).DisconnectHandler();
		view.Handler = null;
	}

	static string CreatePayload(string controlType, int cycle, string slot)
	{
		var prefix = $"{controlType}:{cycle:D3}:{slot}:";
		var fill = (char)('A' + ((cycle + controlType.Length + slot.Length) % 26));
		return prefix + new string(fill, PayloadCharsPerSlot - prefix.Length);
	}

	static void ClearTextView(TextView textView)
	{
		textView.Text = string.Empty;
		textView.Hint = string.Empty;
	}

	static void ClearEditText(EditText editText)
	{
		editText.Text = string.Empty;
		editText.Hint = string.Empty;
	}

	static void ClearSearchView(SearchView searchView)
	{
		searchView.SetQuery(string.Empty, false);
		searchView.QueryHint = string.Empty;

		if (FindDescendant<EditText>(searchView) is { } queryEditor)
			ClearEditText(queryEditor);
	}

	static TextSlotSnapshot CaptureTextSlots(AView view, string controlType)
	{
		var lengths = new List<int>();

		switch (controlType)
		{
			case "Label":
			case "Button":
				if (view is TextView textView)
					AddLength(lengths, textView.Text);
				break;
			case "Entry":
			case "Editor":
				if (view is EditText editText)
				{
					AddLength(lengths, editText.Text);
					AddLength(lengths, editText.Hint);
				}
				break;
			case "SearchBar":
				if (view is SearchView searchView && FindDescendant<EditText>(searchView) is { } queryEditor)
				{
					AddLength(lengths, queryEditor.Text);
					AddLength(lengths, queryEditor.Hint);
				}
				break;
		}

		var payloadSlots = lengths.Count(static length => length >= PayloadCharsPerSlot);
		var retainedBytes = (long)payloadSlots * PayloadBytesPerSlot;
		return new TextSlotSnapshot(lengths.Count, payloadSlots, retainedBytes);
	}

	static void AddLength(List<int> lengths, string? value)
	{
		if (!string.IsNullOrEmpty(value))
			lengths.Add(value.Length);
	}

	static T? FindDescendant<T>(AView view)
		where T : AView
	{
		if (view is T match)
			return match;

		if (view is not ViewGroup group)
			return null;

		for (var i = 0; i < group.ChildCount; i++)
		{
			var child = group.GetChildAt(i);
			if (child is null)
				continue;

			var result = FindDescendant<T>(child);
			if (result is not null)
				return result;
		}

		return null;
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
		string ControlType,
		int Cycle,
		WeakReference<AView> NativePeer,
		WeakReference<object> VirtualView,
		WeakReference<IElementHandler> Handler,
		int ExpectedPayloadSlots)
	{
		public static TrackedCycle Create(
			string controlType,
			int cycle,
			AView platformView,
			object virtualView,
			IElementHandler handler,
			int expectedPayloadSlots)
		{
			return new TrackedCycle(
				controlType,
				cycle,
				new WeakReference<AView>(platformView),
				new WeakReference<object>(virtualView),
				new WeakReference<IElementHandler>(handler),
				expectedPayloadSlots);
		}
	}

	internal sealed record TextSlotSnapshot(
		int AssignedSlots,
		int PayloadSizedSlots,
		long RetainedPayloadBytes);

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int ExpectedPayloadSlots,
		int AliveNativePeers,
		int AliveVirtualViews,
		int AliveHandlers,
		int AssignedTextSlots,
		int PayloadSizedTextSlots,
		long RetainedPayloadBytes,
		IReadOnlyDictionary<string, TypeResult> ByControlType)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var expectedPayloadSlots = 0;
			var aliveNativePeers = 0;
			var aliveVirtualViews = 0;
			var aliveHandlers = 0;
			var assignedTextSlots = 0;
			var payloadSizedTextSlots = 0;
			long retainedPayloadBytes = 0;
			var byType = new Dictionary<string, TypeCounter>(StringComparer.Ordinal);

			foreach (var cycle in tracked)
			{
				var counter = GetCounter(byType, cycle.ControlType);
				counter.Tracked++;
				counter.ExpectedPayloadSlots += cycle.ExpectedPayloadSlots;
				expectedPayloadSlots += cycle.ExpectedPayloadSlots;

				if (cycle.NativePeer.TryGetTarget(out var nativePeer))
				{
					aliveNativePeers++;
					counter.AliveNativePeers++;

					var slots = CaptureTextSlots(nativePeer, cycle.ControlType);
					assignedTextSlots += slots.AssignedSlots;
					payloadSizedTextSlots += slots.PayloadSizedSlots;
					retainedPayloadBytes += slots.RetainedPayloadBytes;
					counter.AssignedTextSlots += slots.AssignedSlots;
					counter.PayloadSizedTextSlots += slots.PayloadSizedSlots;
					counter.RetainedPayloadBytes += slots.RetainedPayloadBytes;
				}

				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				expectedPayloadSlots,
				aliveNativePeers,
				aliveVirtualViews,
				aliveHandlers,
				assignedTextSlots,
				payloadSizedTextSlots,
				retainedPayloadBytes,
				byType.ToDictionary(pair => pair.Key, pair => pair.Value.ToResult(), StringComparer.Ordinal));
		}

		static TypeCounter GetCounter(Dictionary<string, TypeCounter> values, string controlType)
		{
			if (!values.TryGetValue(controlType, out var counter))
			{
				counter = new TypeCounter();
				values.Add(controlType, counter);
			}

			return counter;
		}
	}

	internal sealed record TypeResult(
		int Tracked,
		int ExpectedPayloadSlots,
		int AliveNativePeers,
		int AssignedTextSlots,
		int PayloadSizedTextSlots,
		long RetainedPayloadBytes);

	sealed class TypeCounter
	{
		public int Tracked { get; set; }
		public int ExpectedPayloadSlots { get; set; }
		public int AliveNativePeers { get; set; }
		public int AssignedTextSlots { get; set; }
		public int PayloadSizedTextSlots { get; set; }
		public long RetainedPayloadBytes { get; set; }

		public TypeResult ToResult() =>
			new(Tracked, ExpectedPayloadSlots, AliveNativePeers, AssignedTextSlots, PayloadSizedTextSlots, RetainedPayloadBytes);
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
	int TotalCycles => Cycles * 5;

	public bool LeakProved =>
		Control.AliveNativePeers == TotalCycles &&
		Control.PayloadSizedTextSlots == 0 &&
		Current.AliveNativePeers == TotalCycles &&
		Current.PayloadSizedTextSlots == Current.ExpectedPayloadSlots &&
		Current.RetainedPayloadBytes >= 80L * 1024 * 1024;

	public string ToText()
	{
		var retainedMiB = Current.RetainedPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidNativeTextSlotRetentionRepro",
			$"Cycles per control type: {Cycles}",
			$"Total handler cycles per scenario: {TotalCycles}",
			$"Payload chars per native text slot: {PayloadCharsPerSlot:N0}",
			$"Payload bytes per native text slot: {PayloadBytesPerSlot:N0}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native text payload: {controlMiB:N1} MiB",
			$"Current retained native text payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var lines = new List<string>
		{
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  expected payload text slots: {result.ExpectedPayloadSlots}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive virtual views: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  assigned native text slots: {result.AssignedTextSlots}/{result.ExpectedPayloadSlots}",
			$"  payload-sized native text slots: {result.PayloadSizedTextSlots}/{result.ExpectedPayloadSlots}",
			$"  retained native text payload bytes: {result.RetainedPayloadBytes:N0}"
		};

		foreach (var pair in result.ByControlType.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var value = pair.Value;
			lines.Add(
				$"  {pair.Key}: native={value.AliveNativePeers}/{value.Tracked}, assignedSlots={value.AssignedTextSlots}/{value.ExpectedPayloadSlots}, payloadSlots={value.PayloadSizedTextSlots}/{value.ExpectedPayloadSlots}, retained={value.RetainedPayloadBytes:N0}");
		}

		return string.Join(Environment.NewLine, lines);
	}
}
