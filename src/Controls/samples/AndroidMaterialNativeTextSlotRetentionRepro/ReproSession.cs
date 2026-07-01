#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Views;
using Android.Widget;
using Google.Android.Material.TextField;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AView = Android.Views.View;
using MauiEditor = Microsoft.Maui.Controls.Editor;
using MauiEntry = Microsoft.Maui.Controls.Entry;
using MauiLabel = Microsoft.Maui.Controls.Label;
using MauiRadioButton = Microsoft.Maui.Controls.RadioButton;
using MauiSearchBar = Microsoft.Maui.Controls.SearchBar;

namespace AndroidMaterialNativeTextSlotRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 128;
	const int ControlTypes = 5;
	const int ExpectedPayloadSlotsPerCycle = 8;
	const int PayloadCharsPerSlot = 16 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * sizeof(char);

	static readonly List<AView> RetainedNativePeers = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear Material3 native text and hint slots before disconnect",
			context,
			clearNativeText: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves Material3 native text and hint slots assigned",
			context,
			clearNativeText: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			ControlTypes,
			ExpectedPayloadSlotsPerCycle,
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
		var tracked = new List<TrackedCycle>(Cycles * ControlTypes);

		for (var i = 0; i < Cycles; i++)
		{
			CreateLabelCycle(context, i, tracked, clearNativeText);
			CreateEntryCycle(context, i, tracked, clearNativeText);
			CreateEditorCycle(context, i, tracked, clearNativeText);
			CreateSearchBarCycle(context, i, tracked, clearNativeText);
			CreateRadioButtonCycle(context, i, tracked, clearNativeText);
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
		var handler = AttachMaterialHandler(label, context, "LabelHandler2");
		var platformView = GetPlatformView(handler, "Label");

		if (clearNativeText)
			ClearTextView(GetTextView(platformView, "Label"));

		Disconnect(label, handler);
		label.Text = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("Label", cycle, platformView, label, handler, expectedPayloadSlots: 1));
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
			MaxLength = PayloadCharsPerSlot
		};
		var handler = AttachMaterialHandler(entry, context, "EntryHandler2");
		var platformView = GetPlatformView(handler, "Entry");
		entry.Text = text;
		entry.Placeholder = placeholder;
		handler.UpdateValue(nameof(MauiEntry.Text));
		handler.UpdateValue(nameof(MauiEntry.Placeholder));

		if (clearNativeText)
			ClearTextInputLayout(GetTextInputLayout(platformView, "Entry"));

		Disconnect(entry, handler);
		entry.Text = null;
		entry.Placeholder = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("Entry", cycle, platformView, entry, handler, expectedPayloadSlots: 3));
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
			MaxLength = PayloadCharsPerSlot
		};
		var handler = AttachMaterialHandler(editor, context, "EditorHandler2");
		var platformView = GetPlatformView(handler, "Editor");
		editor.Text = text;
		editor.Placeholder = placeholder;
		handler.UpdateValue(nameof(MauiEditor.Text));
		handler.UpdateValue(nameof(MauiEditor.Placeholder));

		if (clearNativeText)
			ClearTextView(GetTextView(platformView, "Editor"));

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
			MaxLength = PayloadCharsPerSlot
		};
		var handler = AttachMaterialHandler(searchBar, context, "SearchBarHandler2");
		var platformView = GetPlatformView(handler, "SearchBar");
		searchBar.Text = text;
		searchBar.Placeholder = placeholder;
		handler.UpdateValue(nameof(MauiSearchBar.Text));
		handler.UpdateValue(nameof(MauiSearchBar.Placeholder));

		if (clearNativeText)
			ClearTextInputLayout(GetTextInputLayout(platformView, "SearchBar"));

		Disconnect(searchBar, handler);
		searchBar.Text = null;
		searchBar.Placeholder = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("SearchBar", cycle, platformView, searchBar, handler, expectedPayloadSlots: 1));
	}

	static void CreateRadioButtonCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeText)
	{
		var text = CreatePayload("RadioButton", cycle, "Content");
		var radioButton = new MauiRadioButton { Content = text };
		var handler = AttachMaterialHandler(radioButton, context, "RadioButtonHandler2");
		var platformView = GetPlatformView(handler, "RadioButton");

		if (clearNativeText)
			ClearTextView(GetTextView(platformView, "RadioButton"));

		Disconnect(radioButton, handler);
		radioButton.Content = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create("RadioButton", cycle, platformView, radioButton, handler, expectedPayloadSlots: 1));
	}

	static IElementHandler AttachMaterialHandler(IElement view, IMauiContext context, string expectedHandlerName)
	{
		var handler = view.ToHandler(context);
		if (!string.Equals(handler.GetType().Name, expectedHandlerName, StringComparison.Ordinal))
			throw new InvalidOperationException($"Expected Material3 handler {expectedHandlerName}, but got {handler.GetType().FullName}.");

		return handler;
	}

	static AView GetPlatformView(IElementHandler handler, string controlType)
	{
		return handler.PlatformView as AView
			?? throw new InvalidOperationException($"{controlType} Material3 platform view was {handler.PlatformView?.GetType().FullName ?? "null"}, not an Android view.");
	}

	static TextView GetTextView(AView platformView, string controlType)
	{
		return platformView as TextView
			?? throw new InvalidOperationException($"{controlType} Material3 platform view was {platformView.GetType().FullName}, not a TextView.");
	}

	static TextInputLayout GetTextInputLayout(AView platformView, string controlType)
	{
		return platformView as TextInputLayout
			?? throw new InvalidOperationException($"{controlType} Material3 platform view was {platformView.GetType().FullName}, not a TextInputLayout.");
	}

	static void Disconnect(IElement view, IElementHandler handler)
	{
		((IElementHandler)handler).DisconnectHandler();
		view.Handler = null;
	}

	static string CreatePayload(string controlType, int cycle, string slot)
	{
		var prefix = $"{controlType}:{cycle:D4}:{slot}:";
		var fill = (char)('A' + ((cycle + controlType.Length + slot.Length) % 26));
		return prefix + new string(fill, PayloadCharsPerSlot - prefix.Length);
	}

	static void ClearTextView(TextView textView)
	{
		textView.Text = string.Empty;
		textView.Hint = string.Empty;
	}

	static void ClearTextInputLayout(TextInputLayout layout)
	{
		if (layout.EditText is { } editText)
			ClearTextView(editText);

		layout.Hint = string.Empty;
	}

	static TextSlotSnapshot CaptureTextSlots(AView view, string controlType)
	{
		var lengths = new List<int>();

		switch (controlType)
		{
			case "Label":
			case "RadioButton":
				if (view is TextView textView)
					AddTextViewSlots(lengths, textView, includeHint: false);
				break;
			case "Entry":
			case "SearchBar":
				if (view is TextInputLayout layout)
					AddTextInputLayoutSlots(lengths, layout);
				break;
			case "Editor":
				if (view is TextView editText)
					AddTextViewSlots(lengths, editText, includeHint: true);
				break;
		}

		var payloadSlots = lengths.Count(static length => length >= PayloadCharsPerSlot);
		var maxSlotLength = lengths.Count == 0 ? 0 : lengths.Max();
		var retainedBytes = (long)payloadSlots * PayloadBytesPerSlot;
		return new TextSlotSnapshot(lengths.Count, payloadSlots, maxSlotLength, retainedBytes);
	}

	static void AddTextInputLayoutSlots(List<int> lengths, TextInputLayout layout)
	{
		if (layout.EditText is { } editText)
			AddTextViewSlots(lengths, editText, includeHint: true);

		AddLength(lengths, layout.Hint);
	}

	static void AddTextViewSlots(List<int> lengths, TextView textView, bool includeHint)
	{
		AddLength(lengths, textView.Text);

		if (includeHint)
			AddLength(lengths, textView.Hint);
	}

	static void AddLength(List<int> lengths, string? value)
	{
		if (!string.IsNullOrEmpty(value))
			lengths.Add(value.Length);
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
		int MaxSlotLength,
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
		int MaxTextSlotLength,
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
			var maxTextSlotLength = 0;
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
					maxTextSlotLength = Math.Max(maxTextSlotLength, slots.MaxSlotLength);
					retainedPayloadBytes += slots.RetainedPayloadBytes;
					counter.AssignedTextSlots += slots.AssignedSlots;
					counter.PayloadSizedTextSlots += slots.PayloadSizedSlots;
					counter.MaxTextSlotLength = Math.Max(counter.MaxTextSlotLength, slots.MaxSlotLength);
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
				maxTextSlotLength,
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
		int MaxTextSlotLength,
		long RetainedPayloadBytes);

	sealed class TypeCounter
	{
		public int Tracked { get; set; }
		public int ExpectedPayloadSlots { get; set; }
		public int AliveNativePeers { get; set; }
		public int AssignedTextSlots { get; set; }
		public int PayloadSizedTextSlots { get; set; }
		public int MaxTextSlotLength { get; set; }
		public long RetainedPayloadBytes { get; set; }

		public TypeResult ToResult() =>
			new(Tracked, ExpectedPayloadSlots, AliveNativePeers, AssignedTextSlots, PayloadSizedTextSlots, MaxTextSlotLength, RetainedPayloadBytes);
	}
}

internal sealed record ReproReport(
	int Cycles,
	int ControlTypes,
	int ExpectedPayloadSlotsPerCycle,
	int PayloadCharsPerSlot,
	int PayloadBytesPerSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int TotalCycles => Cycles * ControlTypes;
	int TotalExpectedPayloadSlots => Cycles * ExpectedPayloadSlotsPerCycle;

	public bool LeakProved =>
		Control.AliveNativePeers == TotalCycles &&
		Control.PayloadSizedTextSlots == 0 &&
		Current.AliveNativePeers == TotalCycles &&
		Current.PayloadSizedTextSlots >= TotalExpectedPayloadSlots &&
		Current.RetainedPayloadBytes >= 32L * 1024 * 1024;

	public string ToText()
	{
		var retainedMiB = Current.RetainedPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidMaterialNativeTextSlotRetentionRepro",
			$"Cycles per control type: {Cycles}",
			$"Material3 control types per scenario: {ControlTypes}",
			$"Total handler cycles per scenario: {TotalCycles}",
			$"Expected payload text/hint slots per cycle: {ExpectedPayloadSlotsPerCycle}",
			$"Expected payload text/hint slots per scenario: {TotalExpectedPayloadSlots}",
			$"Payload chars per native text/hint slot: {PayloadCharsPerSlot:N0}",
			$"Payload bytes per native text/hint slot: {PayloadBytesPerSlot:N0}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native text/hint payload: {controlMiB:N1} MiB",
			$"Current retained native text/hint payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var lines = new List<string>
		{
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  expected payload text/hint slots: {result.ExpectedPayloadSlots}",
			$"  alive native peers: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive virtual views: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  assigned native text/hint slots: {result.AssignedTextSlots}/{result.ExpectedPayloadSlots}",
			$"  payload-sized native text/hint slots: {result.PayloadSizedTextSlots}/{result.ExpectedPayloadSlots}",
			$"  max native text/hint slot length: {result.MaxTextSlotLength:N0}",
			$"  retained native text/hint payload bytes: {result.RetainedPayloadBytes:N0}"
		};

		foreach (var pair in result.ByControlType.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var value = pair.Value;
			lines.Add(
				$"  {pair.Key}: native={value.AliveNativePeers}/{value.Tracked}, assignedSlots={value.AssignedTextSlots}/{value.ExpectedPayloadSlots}, payloadSlots={value.PayloadSizedTextSlots}/{value.ExpectedPayloadSlots}, maxLength={value.MaxTextSlotLength:N0}, retained={value.RetainedPayloadBytes:N0}");
		}

		return string.Join(Environment.NewLine, lines);
	}
}
