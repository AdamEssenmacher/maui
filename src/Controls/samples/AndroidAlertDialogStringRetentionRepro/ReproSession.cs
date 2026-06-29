#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.AppCompat.Widget;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform;
using AAlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace AndroidAlertDialogStringRetentionRepro;

internal static class ReproSession
{
	const int CyclesPerDialogKind = 96;
	const int ActionSheetItems = 8;
	const int PayloadCharsPerSlot = 8 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * sizeof(char);
	const string PayloadPrefix = "android-alert-dialog-string-";

	static readonly Type DialogBuilderType =
		typeof(Page).Assembly.GetType("Microsoft.Maui.Controls.Platform.AlertManager+AlertRequestHelper+DialogBuilder", throwOnError: true)!;

	static readonly FieldInfo AppCompatAlertDialogField =
		typeof(Page).Assembly
			.GetType("Microsoft.Maui.Controls.Platform.AlertManager+AlertRequestHelper+FlexibleAlertDialog", throwOnError: true)!
			.GetField("_appcompatAlertDialog", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException("FlexibleAlertDialog", "_appcompatAlertDialog");

	static readonly EventHandler<DialogClickEventArgs> NoopClickHandler = static (_, _) =>
	{
	};

	static readonly List<RetainedDialog> RetainedDialogs = new();

	public static async Task<ReproReport> RunAsync(AppCompatActivity activity)
	{
		RetainedDialogs.Clear();

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			activity,
			"control: clear native dialog TextView/ListView/EditText strings after show",
			clearNativeStringSlots: true);

		var current = await RunScenarioAsync(
			activity,
			"current: AlertManager dialog paths leave native dialog strings assigned",
			clearNativeStringSlots: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedDialogs);

		return new ReproReport(
			CyclesPerDialogKind,
			ActionSheetItems,
			PayloadCharsPerSlot,
			PayloadBytesPerSlot,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		AppCompatActivity activity,
		string name,
		bool clearNativeStringSlots)
	{
		var tracked = new List<TrackedDialog>(CyclesPerDialogKind * 3);

		for (var i = 0; i < CyclesPerDialogKind; i++)
		{
			CreateAlertCycle(activity, i, tracked, clearNativeStringSlots);
			CreateActionSheetCycle(activity, i, tracked, clearNativeStringSlots);
			CreatePromptCycle(activity, i, tracked, clearNativeStringSlots);

			if (i % 12 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateAlertCycle(
		AppCompatActivity activity,
		int cycle,
		List<TrackedDialog> tracked,
		bool clearNativeStringSlots)
	{
		var dialog = CreateFlexibleDialog(activity);
		InvokeFlexible(dialog, "SetTitle", CreatePayload(DialogKind.Alert, cycle, "title"));
		InvokeFlexible(dialog, "SetMessage", CreatePayload(DialogKind.Alert, cycle, "message"));
		InvokeFlexible(dialog, "SetButton", (int)DialogButtonType.Positive, CreatePayload(DialogKind.Alert, cycle, "accept"), NoopClickHandler);
		InvokeFlexible(dialog, "SetButton", (int)DialogButtonType.Negative, CreatePayload(DialogKind.Alert, cycle, "cancel"), NoopClickHandler);
		InvokeFlexible(dialog, "Show");

		var nativeDialog = GetNativeDialog(dialog);
		Dismiss(nativeDialog);

		if (clearNativeStringSlots)
			ClearNativeStringSlots(nativeDialog, null);

		var retained = new RetainedDialog(DialogKind.Alert, nativeDialog, null);
		RetainedDialogs.Add(retained);
		tracked.Add(TrackedDialog.Create(retained, dialog));

		dialog = null!;
		nativeDialog = null!;
		retained = null!;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateActionSheetCycle(
		AppCompatActivity activity,
		int cycle,
		List<TrackedDialog> tracked,
		bool clearNativeStringSlots)
	{
		var builder = CreateDialogBuilder(activity);
		InvokeBuilder(builder, "SetTitle", CreatePayload(DialogKind.ActionSheet, cycle, "title"));
		var items = Enumerable.Range(0, ActionSheetItems)
			.Select(item => CreatePayload(DialogKind.ActionSheet, cycle, $"item-{item:D2}"))
			.ToArray();
		InvokeBuilder(builder, "SetItems", items, NoopClickHandler);
		InvokeBuilder(builder, "SetPositiveButton", CreatePayload(DialogKind.ActionSheet, cycle, "cancel"), NoopClickHandler);
		InvokeBuilder(builder, "SetNegativeButton", CreatePayload(DialogKind.ActionSheet, cycle, "destruction"), NoopClickHandler);
		var dialog = InvokeBuilder(builder, "Create");
		InvokeBuilder(builder, "Dispose");
		InvokeFlexible(dialog, "Show");

		var nativeDialog = GetNativeDialog(dialog);
		Dismiss(nativeDialog);

		if (clearNativeStringSlots)
			ClearNativeStringSlots(nativeDialog, null);

		var retained = new RetainedDialog(DialogKind.ActionSheet, nativeDialog, null);
		RetainedDialogs.Add(retained);
		tracked.Add(TrackedDialog.Create(retained, dialog));

		builder = null!;
		dialog = null!;
		nativeDialog = null!;
		retained = null!;
		items = null!;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreatePromptCycle(
		AppCompatActivity activity,
		int cycle,
		List<TrackedDialog> tracked,
		bool clearNativeStringSlots)
	{
		var dialog = CreateFlexibleDialog(activity);
		InvokeFlexible(dialog, "SetTitle", CreatePayload(DialogKind.Prompt, cycle, "title"));
		InvokeFlexible(dialog, "SetMessage", CreatePayload(DialogKind.Prompt, cycle, "message"));

		var frameLayout = new FrameLayout(activity);
		var editText = new AppCompatEditText(activity)
		{
			Hint = CreatePayload(DialogKind.Prompt, cycle, "placeholder"),
			Text = CreatePayload(DialogKind.Prompt, cycle, "initial")
		};
		frameLayout.AddView(editText);
		InvokeFlexible(dialog, "SetView", frameLayout);
		InvokeFlexible(dialog, "SetButton", (int)DialogButtonType.Positive, CreatePayload(DialogKind.Prompt, cycle, "accept"), NoopClickHandler);
		InvokeFlexible(dialog, "SetButton", (int)DialogButtonType.Negative, CreatePayload(DialogKind.Prompt, cycle, "cancel"), NoopClickHandler);
		InvokeFlexible(dialog, "Show");

		var nativeDialog = GetNativeDialog(dialog);
		Dismiss(nativeDialog);

		if (clearNativeStringSlots)
			ClearNativeStringSlots(nativeDialog, editText);

		var retained = new RetainedDialog(DialogKind.Prompt, nativeDialog, editText);
		RetainedDialogs.Add(retained);
		tracked.Add(TrackedDialog.Create(retained, dialog));

		dialog = null!;
		nativeDialog = null!;
		retained = null!;
		frameLayout = null!;
		editText = null!;
	}

	static object CreateDialogBuilder(AppCompatActivity activity) =>
		Activator.CreateInstance(DialogBuilderType, activity)
		?? throw new InvalidOperationException("Could not create AlertManager DialogBuilder.");

	static object CreateFlexibleDialog(AppCompatActivity activity)
	{
		var builder = CreateDialogBuilder(activity);
		var dialog = InvokeBuilder(builder, "Create");
		InvokeBuilder(builder, "Dispose");
		return dialog;
	}

	static object InvokeBuilder(object builder, string method, params object?[] args) =>
		Invoke(builder, method, args);

	static object InvokeFlexible(object dialog, string method, params object?[] args) =>
		Invoke(dialog, method, args);

	static object Invoke(object target, string method, params object?[] args)
	{
		var result = target.GetType().InvokeMember(
			method,
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.InvokeMethod,
			binder: null,
			target,
			args);

		return result ?? target;
	}

	static AAlertDialog GetNativeDialog(object flexibleDialog) =>
		(AAlertDialog)(AppCompatAlertDialogField.GetValue(flexibleDialog)
			?? throw new InvalidOperationException("FlexibleAlertDialog did not contain an AppCompat dialog."));

	static void Dismiss(AAlertDialog dialog)
	{
		if (dialog.IsShowing)
			dialog.Dismiss();
	}

	static void ClearNativeStringSlots(AAlertDialog dialog, AppCompatEditText? promptEditText)
	{
		// Clear public dialog slots first, then clear realized text views.
		dialog.SetTitle(string.Empty);
		dialog.SetMessage(string.Empty);

		for (var button = -3; button <= -1; button++)
		{
			if (dialog.GetButton(button) is { } nativeButton)
				nativeButton.Text = string.Empty;
		}

		if (dialog.ListView is { } listView)
			listView.Adapter = new ArrayAdapter<string>(dialog.Context, Android.Resource.Layout.SimpleListItem1, EmptyStrings(listView.Adapter?.Count ?? 0));

		if (promptEditText is not null)
		{
			promptEditText.Text = string.Empty;
			promptEditText.Hint = string.Empty;
		}

		if (dialog.Window?.DecorView is { } root)
			ClearTextViewDescendants(root);
	}

	static string[] EmptyStrings(int count)
	{
		if (count <= 0)
			return Array.Empty<string>();

		var values = new string[count];
		Array.Fill(values, string.Empty);
		return values;
	}

	static void ClearTextViewDescendants(Android.Views.View view)
	{
		if (view is TextView textView)
		{
			textView.Text = string.Empty;
			textView.Hint = string.Empty;
		}

		if (view is ViewGroup viewGroup)
		{
			for (var i = 0; i < viewGroup.ChildCount; i++)
			{
				if (viewGroup.GetChildAt(i) is { } child)
					ClearTextViewDescendants(child);
			}
		}
	}

	static TextSlotSnapshot CaptureNativeStringSlots(RetainedDialog retained)
	{
		var assigned = 0;
		var payload = 0;
		long bytes = 0;

		if (retained.Dialog.Window?.DecorView is { } root)
			AccumulateTextViewDescendants(root, ref assigned, ref payload, ref bytes);

		AccumulateListAdapter(retained.Dialog.ListView?.Adapter, ref assigned, ref payload, ref bytes);

		if (retained.PromptEditText is not null)
		{
			Accumulate(retained.PromptEditText.Text, ref assigned, ref payload, ref bytes);
			Accumulate(retained.PromptEditText.Hint, ref assigned, ref payload, ref bytes);
		}

		return new TextSlotSnapshot(assigned, payload, bytes);
	}

	static void AccumulateTextViewDescendants(Android.Views.View view, ref int assigned, ref int payload, ref long bytes)
	{
		if (view is TextView textView)
		{
			Accumulate(textView.Text, ref assigned, ref payload, ref bytes);
			Accumulate(textView.Hint, ref assigned, ref payload, ref bytes);
		}

		if (view is ViewGroup viewGroup)
		{
			for (var i = 0; i < viewGroup.ChildCount; i++)
			{
				if (viewGroup.GetChildAt(i) is { } child)
					AccumulateTextViewDescendants(child, ref assigned, ref payload, ref bytes);
			}
		}
	}

	static void AccumulateListAdapter(IListAdapter? adapter, ref int assigned, ref int payload, ref long bytes)
	{
		if (adapter is null)
			return;

		for (var i = 0; i < adapter.Count; i++)
			Accumulate(adapter.GetItem(i)?.ToString(), ref assigned, ref payload, ref bytes);
	}

	static void Accumulate(string? text, ref int assigned, ref int payload, ref long bytes)
	{
		if (string.IsNullOrEmpty(text))
			return;

		assigned++;
		bytes += (long)text.Length * sizeof(char);

		if (text.StartsWith(PayloadPrefix, StringComparison.Ordinal) &&
			text.Length >= PayloadCharsPerSlot)
		{
			payload++;
		}
	}

	static string CreatePayload(DialogKind kind, int cycle, string slot)
	{
		var prefix = $"{PayloadPrefix}{kind.ToString().ToLowerInvariant()}-{slot}-{cycle:D4}-";
		return prefix + new string((char)('A' + (cycle % 26)), PayloadCharsPerSlot - prefix.Length);
	}

	static int CountAlive<T>(IEnumerable<WeakReference<T>> references)
		where T : class
	{
		var count = 0;
		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out _))
				count++;
		}

		return count;
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

	internal enum DialogKind
	{
		Alert,
		ActionSheet,
		Prompt
	}

	internal sealed record TextSlotSnapshot(int AssignedSlots, int PayloadSlots, long RetainedBytes);

	internal sealed record RetainedDialog(DialogKind Kind, AAlertDialog Dialog, AppCompatEditText? PromptEditText);

	internal sealed record TrackedDialog(
		DialogKind Kind,
		RetainedDialog Retained,
		WeakReference<object> FlexibleDialog)
	{
		public static TrackedDialog Create(RetainedDialog retained, object flexibleDialog) =>
			new(retained.Kind, retained, new WeakReference<object>(flexibleDialog));
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedDialogs,
		int AliveNativeDialogs,
		int AliveFlexibleDialogWrappers,
		int AlertDialogs,
		int ActionSheetDialogs,
		int PromptDialogs,
		int AssignedNativeStringSlots,
		int PayloadNativeStringSlots,
		long RetainedNativeStringBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedDialog> tracked)
		{
			var wrapperRefs = new List<WeakReference<object>>();
			var aliveNativeDialogs = 0;
			var alertDialogs = 0;
			var actionSheetDialogs = 0;
			var promptDialogs = 0;
			var assignedSlots = 0;
			var payloadSlots = 0;
			long retainedBytes = 0;

			foreach (var item in tracked)
			{
				wrapperRefs.Add(item.FlexibleDialog);

				if (item.Retained.Dialog.Handle != IntPtr.Zero)
				{
					aliveNativeDialogs++;
					switch (item.Kind)
					{
						case DialogKind.Alert:
							alertDialogs++;
							break;
						case DialogKind.ActionSheet:
							actionSheetDialogs++;
							break;
						case DialogKind.Prompt:
							promptDialogs++;
							break;
					}

					var snapshot = CaptureNativeStringSlots(item.Retained);
					assignedSlots += snapshot.AssignedSlots;
					payloadSlots += snapshot.PayloadSlots;
					retainedBytes += snapshot.RetainedBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeDialogs,
				CountAlive(wrapperRefs),
				alertDialogs,
				actionSheetDialogs,
				promptDialogs,
				assignedSlots,
				payloadSlots,
				retainedBytes);
		}
	}
}

internal sealed record ReproReport(
	int CyclesPerDialogKind,
	int ActionSheetItems,
	int PayloadCharsPerSlot,
	int PayloadBytesPerSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public int ExpectedDialogs => CyclesPerDialogKind * 3;

	public int ExpectedPayloadSlots =>
		CyclesPerDialogKind *
		(4 + (1 + ActionSheetItems + 2) + 6);

	public bool LeakProved =>
		Control.AliveNativeDialogs == ExpectedDialogs &&
		Current.AliveNativeDialogs == ExpectedDialogs &&
		Control.PayloadNativeStringSlots == 0 &&
		Current.PayloadNativeStringSlots >= ExpectedPayloadSlots &&
		Current.AliveFlexibleDialogWrappers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidAlertDialogStringRetentionRepro",
			$"Cycles per dialog kind: {CyclesPerDialogKind}",
			$"Action sheet generated item count: {ActionSheetItems}",
			$"Payload chars per native dialog string slot: {PayloadCharsPerSlot}",
			$"Payload bytes per native dialog string slot: {PayloadBytesPerSlot}",
			$"Expected retained native dialog peers: {ExpectedDialogs}",
			$"Expected payload slots: {ExpectedPayloadSlots}",
			"Source paths mirrored: AlertManager Alert/DialogBuilder SetTitle, SetMessage, SetButton, SetItems, SetView, and prompt AppCompatEditText text/hint assignment",
			"Click callbacks neutralized in both runs to isolate native dialog string slots from callback/arguments retention",
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
			$"  tracked dialogs: {result.TrackedDialogs}",
			$"  alive native AlertDialog peers: {result.AliveNativeDialogs}/{result.TrackedDialogs}",
			$"  alive AlertManager FlexibleAlertDialog wrappers: {result.AliveFlexibleDialogWrappers}/{result.TrackedDialogs}",
			$"  alive alert dialogs: {result.AlertDialogs}",
			$"  alive action sheet dialogs: {result.ActionSheetDialogs}",
			$"  alive prompt dialogs: {result.PromptDialogs}",
			$"  assigned native string slots: {result.AssignedNativeStringSlots}",
			$"  payload-sized native string slots: {result.PayloadNativeStringSlots}",
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
