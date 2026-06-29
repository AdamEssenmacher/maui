#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Views;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using AView = Android.Views.View;
using AppCompatAlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace AndroidPickerDialogStringRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 128;
	const int ItemsPerPicker = 16;
	const int PayloadCharsPerTitle = 8 * 1024;
	const int PayloadCharsPerItem = 8 * 1024;
	const int PayloadBytesPerTitle = PayloadCharsPerTitle * sizeof(char);
	const int PayloadBytesPerItem = PayloadCharsPerItem * sizeof(char);

	static readonly FieldInfo DialogField =
		typeof(PickerHandler).GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(PickerHandler), "_dialog");

	static readonly List<AppCompatAlertDialog> RetainedDialogs = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native dialog title/list strings before disconnect",
			context,
			clearNativeDialogStrings: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect dismisses dialog but leaves native title/list strings assigned",
			context,
			clearNativeDialogStrings: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedDialogs);

		return new ReproReport(
			Cycles,
			ItemsPerPicker,
			PayloadCharsPerTitle,
			PayloadCharsPerItem,
			PayloadBytesPerTitle,
			PayloadBytesPerItem,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeDialogStrings)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(context, i, tracked, clearNativeDialogStrings);

			if (i % 16 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeDialogStrings)
	{
		var picker = new Picker
		{
			Title = CreateTitlePayload(cycle),
			SelectedIndex = 0
		};

		for (var item = 0; item < ItemsPerPicker; item++)
			picker.Items.Add(CreateItemPayload(cycle, item));

		var handler = new PickerHandler();
		AttachHandler(picker, handler, context);
		PickerHandler.MapTitle(handler, picker);

		handler.PlatformView.CallOnClick();

		if (DialogField.GetValue(handler) is not AppCompatAlertDialog dialog)
			throw new InvalidOperationException("PickerHandler did not create an AlertDialog.");

		NeutralizeDialogManagedCallbacks(dialog);

		if (clearNativeDialogStrings)
			ClearNativeDialogStrings(dialog);

		Disconnect(picker, handler);
		picker.Title = null;
		picker.Items.Clear();

		RetainedDialogs.Add(dialog);
		tracked.Add(TrackedCycle.Create(cycle, dialog, picker, handler));
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

	static void NeutralizeDialogManagedCallbacks(AppCompatAlertDialog dialog)
	{
		if (dialog.ListView is { } listView)
			listView.OnItemClickListener = null;

		dialog.GetButton((int)DialogButtonType.Negative)?.SetOnClickListener(null);
	}

	static void ClearNativeDialogStrings(AppCompatAlertDialog dialog)
	{
		ClearDescendantTextViews(dialog.Window?.DecorView);

		if (dialog.ListView is { } listView)
		{
			ClearDescendantTextViews(listView);
			listView.Adapter = null;
		}
	}

	static DialogStringSnapshot CaptureDialogStrings(AppCompatAlertDialog dialog)
	{
		var assignedTitleSlots = 0;
		var titlePayloadSlots = 0;
		var assignedItemSlots = 0;
		var payloadItemSlots = 0;
		long retainedNativeStringBytes = 0;

		if (dialog.ListView?.Adapter is { } adapter)
		{
			for (var i = 0; i < adapter.Count; i++)
			{
				var length = adapter.GetItem(i)?.ToString()?.Length ?? 0;
				if (length <= 0)
					continue;

				assignedItemSlots++;
				retainedNativeStringBytes += (long)length * sizeof(char);

				if (length >= PayloadCharsPerItem)
					payloadItemSlots++;
			}
		}

		foreach (var text in GetDescendantTexts(dialog.Window?.DecorView))
		{
			if (text.StartsWith("android-picker-dialog-title-", StringComparison.Ordinal))
			{
				assignedTitleSlots++;
				retainedNativeStringBytes += (long)text.Length * sizeof(char);

				if (text.Length >= PayloadCharsPerTitle)
					titlePayloadSlots++;
			}
		}

		return new DialogStringSnapshot(
			assignedTitleSlots,
			titlePayloadSlots,
			assignedItemSlots,
			payloadItemSlots,
			retainedNativeStringBytes);
	}

	static IEnumerable<string> GetDescendantTexts(AView? view)
	{
		if (view is null)
			yield break;

		if (view is TextView textView)
		{
			var text = textView.Text;
			if (!string.IsNullOrEmpty(text))
				yield return text;
		}

		if (view is not ViewGroup group)
			yield break;

		for (var i = 0; i < group.ChildCount; i++)
		{
			foreach (var text in GetDescendantTexts(group.GetChildAt(i)))
				yield return text;
		}
	}

	static void ClearDescendantTextViews(AView? view)
	{
		if (view is null)
			return;

		if (view is TextView textView)
			textView.Text = string.Empty;

		if (view is not ViewGroup group)
			return;

		for (var i = 0; i < group.ChildCount; i++)
		{
			if (group.GetChildAt(i) is { } child)
				ClearDescendantTextViews(child);
		}
	}

	static string CreateTitlePayload(int cycle)
	{
		var prefix = $"android-picker-dialog-title-{cycle:D4}-";
		return prefix + new string((char)('A' + (cycle % 26)), PayloadCharsPerTitle - prefix.Length);
	}

	static string CreateItemPayload(int cycle, int item)
	{
		var prefix = $"android-picker-dialog-item-{cycle:D4}-{item:D2}-";
		return prefix + new string((char)('a' + ((cycle + item) % 26)), PayloadCharsPerItem - prefix.Length);
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
		WeakReference<AppCompatAlertDialog> Dialog,
		WeakReference<object> VirtualPicker,
		WeakReference<PickerHandler> Handler)
	{
		public static TrackedCycle Create(
			int cycle,
			AppCompatAlertDialog dialog,
			object virtualPicker,
			PickerHandler handler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<AppCompatAlertDialog>(dialog),
				new WeakReference<object>(virtualPicker),
				new WeakReference<PickerHandler>(handler));
		}
	}

	internal sealed record DialogStringSnapshot(
		int AssignedTitleSlots,
		int PayloadTitleSlots,
		int AssignedItemSlots,
		int PayloadItemSlots,
		long RetainedNativeStringBytes);

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveDialogs,
		int AliveVirtualPickers,
		int AliveHandlers,
		int AssignedTitleSlots,
		int PayloadTitleSlots,
		int AssignedItemSlots,
		int PayloadItemSlots,
		long RetainedNativeStringBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveDialogs = 0;
			var aliveVirtualPickers = 0;
			var aliveHandlers = 0;
			var assignedTitleSlots = 0;
			var payloadTitleSlots = 0;
			var assignedItemSlots = 0;
			var payloadItemSlots = 0;
			long retainedNativeStringBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Dialog.TryGetTarget(out var dialog))
				{
					aliveDialogs++;
					var snapshot = CaptureDialogStrings(dialog);
					assignedTitleSlots += snapshot.AssignedTitleSlots;
					payloadTitleSlots += snapshot.PayloadTitleSlots;
					assignedItemSlots += snapshot.AssignedItemSlots;
					payloadItemSlots += snapshot.PayloadItemSlots;
					retainedNativeStringBytes += snapshot.RetainedNativeStringBytes;
				}

				if (cycle.VirtualPicker.TryGetTarget(out _))
					aliveVirtualPickers++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveDialogs,
				aliveVirtualPickers,
				aliveHandlers,
				assignedTitleSlots,
				payloadTitleSlots,
				assignedItemSlots,
				payloadItemSlots,
				retainedNativeStringBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int ItemsPerPicker,
	int PayloadCharsPerTitle,
	int PayloadCharsPerItem,
	int PayloadBytesPerTitle,
	int PayloadBytesPerItem,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public int ExpectedItemPayloadSlots => Cycles * ItemsPerPicker;

	public bool LeakProved =>
		Control.AliveDialogs == Cycles &&
		Current.AliveDialogs == Cycles &&
		Control.PayloadTitleSlots == 0 &&
		Control.PayloadItemSlots == 0 &&
		Current.PayloadItemSlots >= ExpectedItemPayloadSlots &&
		Current.AliveVirtualPickers == 0 &&
		Current.AliveHandlers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidPickerDialogStringRetentionRepro",
			$"Cycles: {Cycles}",
			$"Items per picker: {ItemsPerPicker}",
			$"Payload chars per native dialog title slot: {PayloadCharsPerTitle}",
			$"Payload chars per native dialog item slot: {PayloadCharsPerItem}",
			$"Payload bytes per native dialog title slot: {PayloadBytesPerTitle}",
			$"Payload bytes per native dialog item slot: {PayloadBytesPerItem}",
			$"Expected native item payload slots: {ExpectedItemPayloadSlots}",
			$"Managed callback neutralization: native ListView item-click listener cleared in both runs",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native dialog string payload: {FormatBytes(Control.RetainedNativeStringBytes)}",
			$"Current retained native dialog string payload: {FormatBytes(Current.RetainedNativeStringBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native dialogs: {result.AliveDialogs}/{result.TrackedCycles}",
			$"  alive virtual pickers: {result.AliveVirtualPickers}/{result.TrackedCycles}",
			$"  alive picker handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  assigned native title slots: {result.AssignedTitleSlots}/{result.TrackedCycles}",
			$"  payload-sized native title slots: {result.PayloadTitleSlots}/{result.TrackedCycles}",
			$"  assigned native item slots: {result.AssignedItemSlots}",
			$"  payload-sized native item slots: {result.PayloadItemSlots}",
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
