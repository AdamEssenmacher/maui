#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using AndroidX.AppCompat.App;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;

namespace AndroidAlertManagerHandlerChangedRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int ActionSheetItems = 6;
	const int PayloadCharsPerSlot = 16 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * sizeof(char);
	const string PayloadPrefix = "android-alertmanager-handlerchanged-";

	static readonly Type AlertRequestHelperType =
		typeof(Page).Assembly.GetType("Microsoft.Maui.Controls.Platform.AlertManager+AlertRequestHelper", throwOnError: true)!;

	static readonly FieldInfo HandlerChangedField =
		typeof(Element).GetField("HandlerChanged", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(Element), "HandlerChanged");

	static readonly MethodInfo OnAlertRequestedMethod =
		AlertRequestHelperType.GetMethod("OnAlertRequested", BindingFlags.Instance | BindingFlags.Public)
		?? throw new MissingMethodException(AlertRequestHelperType.FullName, "OnAlertRequested");

	static readonly MethodInfo OnActionSheetRequestedMethod =
		AlertRequestHelperType.GetMethod("OnActionSheetRequested", BindingFlags.Instance | BindingFlags.Public)
		?? throw new MissingMethodException(AlertRequestHelperType.FullName, "OnActionSheetRequested");

	static readonly MethodInfo OnPromptRequestedMethod =
		AlertRequestHelperType.GetMethod("OnPromptRequested", BindingFlags.Instance | BindingFlags.Public)
		?? throw new MissingMethodException(AlertRequestHelperType.FullName, "OnPromptRequested");

	static readonly List<Page> RetainedPages = new();
	static readonly List<object> RetainedHelpers = new();

	public static async Task<ReproReport> RunAsync(AppCompatActivity activity)
	{
		RetainedPages.Clear();
		RetainedHelpers.Clear();

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			activity,
			"control: clear HandlerChanged pending alert callbacks after queuing",
			clearHandlerChangedAfterQueue: true);

		var current = await RunScenarioAsync(
			activity,
			"current: handlerless AlertManager requests leave HandlerChanged callbacks queued",
			clearHandlerChangedAfterQueue: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedPages);
		GC.KeepAlive(RetainedHelpers);

		return new ReproReport(
			Cycles,
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
		bool clearHandlerChangedAfterQueue)
	{
		var helper = CreateAlertRequestHelper(activity);
		RetainedHelpers.Add(helper);

		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			tracked.Add(CreatePendingDialogCycle(helper, i, clearHandlerChangedAfterQueue));

			if (i % 10 == 0)
				await Task.Yield();
		}

		helper = null!;

		await Task.Delay(100);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static TrackedCycle CreatePendingDialogCycle(
		object helper,
		int cycle,
		bool clearHandlerChangedAfterQueue)
	{
		var page = new ContentPage();
		RetainedPages.Add(page);

		var alertPayloads = new List<string>
		{
			CreatePayload(DialogKind.Alert, cycle, "title"),
			CreatePayload(DialogKind.Alert, cycle, "message"),
			CreatePayload(DialogKind.Alert, cycle, "accept"),
			CreatePayload(DialogKind.Alert, cycle, "cancel")
		};
		var alertArguments = new AlertArguments(alertPayloads[0], alertPayloads[1], alertPayloads[2], alertPayloads[3]);

		var actionSheetPayloads = new List<string>
		{
			CreatePayload(DialogKind.ActionSheet, cycle, "title"),
			CreatePayload(DialogKind.ActionSheet, cycle, "cancel"),
			CreatePayload(DialogKind.ActionSheet, cycle, "destruction")
		};
		actionSheetPayloads.AddRange(Enumerable.Range(0, ActionSheetItems)
			.Select(item => CreatePayload(DialogKind.ActionSheet, cycle, $"item-{item:D2}")));
		var actionSheetArguments = new ActionSheetArguments(
			actionSheetPayloads[0],
			actionSheetPayloads[1],
			actionSheetPayloads[2],
			actionSheetPayloads.Skip(3).ToArray());

		var promptPayloads = new List<string>
		{
			CreatePayload(DialogKind.Prompt, cycle, "title"),
			CreatePayload(DialogKind.Prompt, cycle, "message"),
			CreatePayload(DialogKind.Prompt, cycle, "accept"),
			CreatePayload(DialogKind.Prompt, cycle, "cancel"),
			CreatePayload(DialogKind.Prompt, cycle, "placeholder"),
			CreatePayload(DialogKind.Prompt, cycle, "initial")
		};
		var promptArguments = new PromptArguments(
			promptPayloads[0],
			promptPayloads[1],
			promptPayloads[2],
			promptPayloads[3],
			promptPayloads[4],
			initialValue: promptPayloads[5]);

		OnAlertRequestedMethod.Invoke(helper, new object[] { page, alertArguments });
		OnActionSheetRequestedMethod.Invoke(helper, new object[] { page, actionSheetArguments });
		OnPromptRequestedMethod.Invoke(helper, new object[] { page, promptArguments });

		var queuedHandlersAfterRequest = CountHandlerChangedHandlers(page);

		if (clearHandlerChangedAfterQueue)
			ClearHandlerChangedHandlers(page);

		var queuedHandlersAfterCleanup = CountHandlerChangedHandlers(page);

		var tracked = TrackedCycle.Create(
			page,
			alertArguments,
			actionSheetArguments,
			promptArguments,
			alertPayloads,
			actionSheetPayloads,
			promptPayloads,
			queuedHandlersAfterRequest,
			queuedHandlersAfterCleanup);

		page = null!;
		alertArguments = null!;
		actionSheetArguments = null!;
		promptArguments = null!;
		alertPayloads = null!;
		actionSheetPayloads = null!;
		promptPayloads = null!;

		return tracked;
	}

	static object CreateAlertRequestHelper(Activity activity)
	{
		return Activator.CreateInstance(
			AlertRequestHelperType,
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			args: new object?[] { activity, null },
			culture: null)
			?? throw new InvalidOperationException("Could not create AlertManager.AlertRequestHelper.");
	}

	static int CountHandlerChangedHandlers(Element element)
	{
		if (HandlerChangedField.GetValue(element) is not Delegate handler)
			return 0;

		return handler.GetInvocationList().Length;
	}

	static void ClearHandlerChangedHandlers(Element element) =>
		HandlerChangedField.SetValue(element, null);

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

	internal sealed record TrackedCycle(
		WeakReference<Page> Page,
		WeakReference<AlertArguments> AlertArguments,
		WeakReference<ActionSheetArguments> ActionSheetArguments,
		WeakReference<PromptArguments> PromptArguments,
		IReadOnlyList<WeakReference<string>> AlertPayloads,
		IReadOnlyList<WeakReference<string>> ActionSheetPayloads,
		IReadOnlyList<WeakReference<string>> PromptPayloads,
		int HandlerChangedHandlersAfterRequest,
		int HandlerChangedHandlersAfterCleanup)
	{
		public static TrackedCycle Create(
			Page page,
			AlertArguments alertArguments,
			ActionSheetArguments actionSheetArguments,
			PromptArguments promptArguments,
			IReadOnlyList<string> alertPayloads,
			IReadOnlyList<string> actionSheetPayloads,
			IReadOnlyList<string> promptPayloads,
			int handlerChangedHandlersAfterRequest,
			int handlerChangedHandlersAfterCleanup) =>
			new(
				new WeakReference<Page>(page),
				new WeakReference<AlertArguments>(alertArguments),
				new WeakReference<ActionSheetArguments>(actionSheetArguments),
				new WeakReference<PromptArguments>(promptArguments),
				alertPayloads.Select(static payload => new WeakReference<string>(payload)).ToArray(),
				actionSheetPayloads.Select(static payload => new WeakReference<string>(payload)).ToArray(),
				promptPayloads.Select(static payload => new WeakReference<string>(payload)).ToArray(),
				handlerChangedHandlersAfterRequest,
				handlerChangedHandlersAfterCleanup);
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedPages,
		int AliveRetainedPages,
		int AliveAlertArguments,
		int AliveActionSheetArguments,
		int AlivePromptArguments,
		int AlivePayloadStrings,
		long RetainedPayloadBytes,
		int HandlerChangedHandlersAfterRequest,
		int HandlerChangedHandlersAfterCleanup)
	{
		internal int AliveArgumentObjects => AliveAlertArguments + AliveActionSheetArguments + AlivePromptArguments;

		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var pageRefs = new List<WeakReference<Page>>();
			var alertRefs = new List<WeakReference<AlertArguments>>();
			var actionSheetRefs = new List<WeakReference<ActionSheetArguments>>();
			var promptRefs = new List<WeakReference<PromptArguments>>();
			var payloadRefs = new List<WeakReference<string>>();
			var handlersAfterRequest = 0;
			var handlersAfterCleanup = 0;

			foreach (var item in tracked)
			{
				pageRefs.Add(item.Page);
				alertRefs.Add(item.AlertArguments);
				actionSheetRefs.Add(item.ActionSheetArguments);
				promptRefs.Add(item.PromptArguments);
				payloadRefs.AddRange(item.AlertPayloads);
				payloadRefs.AddRange(item.ActionSheetPayloads);
				payloadRefs.AddRange(item.PromptPayloads);
				handlersAfterRequest += item.HandlerChangedHandlersAfterRequest;
				handlersAfterCleanup += item.HandlerChangedHandlersAfterCleanup;
			}

			var alivePayloads = CountAlive(payloadRefs);

			return new ScenarioResult(
				name,
				tracked.Count,
				CountAlive(pageRefs),
				CountAlive(alertRefs),
				CountAlive(actionSheetRefs),
				CountAlive(promptRefs),
				alivePayloads,
				(long)alivePayloads * PayloadBytesPerSlot,
				handlersAfterRequest,
				handlersAfterCleanup);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int ActionSheetItems,
	int PayloadCharsPerSlot,
	int PayloadBytesPerSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public int ExpectedPayloadStringsPerCycle => 4 + 3 + ActionSheetItems + 6;
	public int ExpectedPayloadStrings => Cycles * ExpectedPayloadStringsPerCycle;
	public int ExpectedArgumentObjects => Cycles * 3;
	public int ExpectedQueuedHandlers => Cycles * 3;

	public bool LeakProved =>
		Control.AliveRetainedPages == Cycles &&
		Current.AliveRetainedPages == Cycles &&
		Control.AliveArgumentObjects == 0 &&
		Control.AlivePayloadStrings == 0 &&
		Control.HandlerChangedHandlersAfterCleanup == 0 &&
		Current.AliveArgumentObjects == ExpectedArgumentObjects &&
		Current.AlivePayloadStrings == ExpectedPayloadStrings &&
		Current.HandlerChangedHandlersAfterCleanup == ExpectedQueuedHandlers;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidAlertManagerHandlerChangedRetentionRepro",
			$"Cycles: {Cycles}",
			$"Action sheet generated item count: {ActionSheetItems}",
			$"Payload chars per dialog string slot: {PayloadCharsPerSlot}",
			$"Payload bytes per dialog string slot: {PayloadBytesPerSlot}",
			$"Expected retained handlerless pages per run: {Cycles}",
			$"Expected dialog argument objects per run: {ExpectedArgumentObjects}",
			$"Expected payload strings per run: {ExpectedPayloadStrings}",
			$"Expected queued HandlerChanged callbacks per current run: {ExpectedQueuedHandlers}",
			"Source path exercised: Android AlertManager.AlertRequestHelper WaitForHandlerIfNeeded for alert, action-sheet, and prompt requests",
			"Both runs retain only handlerless pages; control clears Element.HandlerChanged after requests are queued",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained dialog payload: {FormatBytes(Control.RetainedPayloadBytes)}",
			$"Current retained dialog payload: {FormatBytes(Current.RetainedPayloadBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked handlerless pages: {result.TrackedPages}",
			$"  alive retained pages: {result.AliveRetainedPages}/{result.TrackedPages}",
			$"  alive AlertArguments: {result.AliveAlertArguments}/{result.TrackedPages}",
			$"  alive ActionSheetArguments: {result.AliveActionSheetArguments}/{result.TrackedPages}",
			$"  alive PromptArguments: {result.AlivePromptArguments}/{result.TrackedPages}",
			$"  alive dialog argument objects: {result.AliveArgumentObjects}",
			$"  alive payload strings: {result.AlivePayloadStrings}",
			$"  retained payload bytes: {result.RetainedPayloadBytes:N0}",
			$"  HandlerChanged callbacks immediately after request: {result.HandlerChangedHandlersAfterRequest}",
			$"  HandlerChanged callbacks after scenario cleanup: {result.HandlerChangedHandlersAfterCleanup}");
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
