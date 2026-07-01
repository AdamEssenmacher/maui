#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Widget;
using Google.Android.Material.Dialog;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AResource = Android.Resource;
using AView = Android.Views.View;
using AppCompatAlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace AndroidPickerDialogCallbackRetentionRepro;

internal static class ReproSession
{
	const int CyclesPerHandlerType = 48;
	const int HandlerTypes = 2;
	const int ItemsPerPicker = 4;
	const int ContextPayloadBytes = 512 * 1024;

	static readonly FieldInfo ClassicDialogField =
		typeof(PickerHandler).GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(PickerHandler).FullName, "_dialog");

	static readonly Type MaterialPickerHandlerType =
		typeof(PickerHandler).Assembly.GetType("Microsoft.Maui.Handlers.PickerHandler2")
		?? throw new MissingMemberException("Microsoft.Maui.Handlers.PickerHandler2");

	static readonly FieldInfo MaterialDialogField =
		MaterialPickerHandlerType.GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(MaterialPickerHandlerType.FullName, "_dialog");

	static readonly List<AppCompatAlertDialog> RetainedDialogRoots = new();

	public static async Task<ReproReport> RunAsync(IMauiContext rootContext)
	{
		RetainedDialogRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var androidContext = rootContext.Context
			?? Android.App.Application.Context
			?? throw new InvalidOperationException("No Android context is available.");

		var control = await RunScenarioAsync(
			rootContext.Services,
			androidContext,
			"control: retained native picker dialogs with weak item callbacks",
			useWeakCallbackDialogs: true);

		var current = await RunScenarioAsync(
			rootContext.Services,
			androidContext,
			"current: retained native picker dialogs keep item callbacks that capture disconnected handlers",
			useWeakCallbackDialogs: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedDialogRoots);

		return new ReproReport(
			CyclesPerHandlerType,
			HandlerTypes,
			ItemsPerPicker,
			ContextPayloadBytes,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		IServiceProvider rootServices,
		Context androidContext,
		string name,
		bool useWeakCallbackDialogs)
	{
		var tracked = new List<TrackedCycle>(CyclesPerHandlerType * HandlerTypes);

		for (var i = 0; i < CyclesPerHandlerType; i++)
		{
			CreateClassicCycle(rootServices, androidContext, i, tracked, useWeakCallbackDialogs);
			CreateMaterialCycle(rootServices, androidContext, i, tracked, useWeakCallbackDialogs);

			if (i % 8 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateClassicCycle(
		IServiceProvider rootServices,
		Context androidContext,
		int cycle,
		List<TrackedCycle> tracked,
		bool useWeakCallbackDialog)
	{
		var serviceProvider = new PayloadServiceProvider(rootServices, $"classic-{cycle:D4}", ContextPayloadBytes);
		var cycleContext = new MauiContext(serviceProvider, androidContext);
		var picker = CreatePicker(cycle, "classic");
		var handler = new PickerHandler();

		AttachHandler(picker, handler, cycleContext, "PickerHandler");

		var dialog = useWeakCallbackDialog
			? CreateAndAssignWeakCallbackDialog(androidContext, picker, handler, ClassicDialogField)
			: OpenCurrentDialog(handler, ClassicDialogField);

		Disconnect(picker, handler);
		ClearPicker(picker);

		RetainedDialogRoots.Add(dialog);
		tracked.Add(TrackedCycle.Create("PickerHandler", cycle, dialog, picker, handler, cycleContext, serviceProvider));

		serviceProvider = null!;
		cycleContext = null!;
		picker = null!;
		handler = null!;
		dialog = null!;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateMaterialCycle(
		IServiceProvider rootServices,
		Context androidContext,
		int cycle,
		List<TrackedCycle> tracked,
		bool useWeakCallbackDialog)
	{
		var serviceProvider = new PayloadServiceProvider(rootServices, $"material-{cycle:D4}", ContextPayloadBytes);
		var cycleContext = new MauiContext(serviceProvider, androidContext);
		var picker = CreatePicker(cycle, "material");
		var handler = AttachMaterialHandler(picker, cycleContext, "PickerHandler2");

		var dialog = useWeakCallbackDialog
			? CreateAndAssignWeakCallbackDialog(androidContext, picker, handler, MaterialDialogField)
			: OpenCurrentDialog(handler, MaterialDialogField);

		Disconnect(picker, handler);
		ClearPicker(picker);

		RetainedDialogRoots.Add(dialog);
		tracked.Add(TrackedCycle.Create("PickerHandler2", cycle, dialog, picker, handler, cycleContext, serviceProvider));

		serviceProvider = null!;
		cycleContext = null!;
		picker = null!;
		handler = null!;
		dialog = null!;
	}

	static Picker CreatePicker(int cycle, string kind)
	{
		var picker = new Picker
		{
			Title = $"callback retention picker {kind} {cycle:D4}",
			SelectedIndex = 0
		};

		for (var i = 0; i < ItemsPerPicker; i++)
			picker.Items.Add($"item {kind} {cycle:D4}-{i:D2}");

		return picker;
	}

	static void ClearPicker(Picker picker)
	{
		picker.SelectedIndex = -1;
		picker.Title = null;
		picker.Items.Clear();
	}

	static void AttachHandler(IElement view, IElementHandler handler, IMauiContext context, string expectedHandlerName)
	{
		handler.SetMauiContext(context);
		view.Handler = handler;

		if (!string.Equals(handler.GetType().Name, expectedHandlerName, StringComparison.Ordinal))
			throw new InvalidOperationException($"Expected handler {expectedHandlerName}, but got {handler.GetType().FullName}.");
	}

	static IElementHandler AttachMaterialHandler(IElement view, IMauiContext context, string expectedHandlerName)
	{
		var handler = view.ToHandler(context);
		if (!string.Equals(handler.GetType().Name, expectedHandlerName, StringComparison.Ordinal))
			throw new InvalidOperationException($"Expected Material3 handler {expectedHandlerName}, but got {handler.GetType().FullName}.");

		return handler;
	}

	static AppCompatAlertDialog OpenCurrentDialog(IElementHandler handler, FieldInfo dialogField)
	{
		if (handler.PlatformView is not AView platformView)
			throw new InvalidOperationException($"Handler platform view was {handler.PlatformView?.GetType().FullName ?? "null"}, not an Android View.");

		platformView.CallOnClick();

		return dialogField.GetValue(handler) as AppCompatAlertDialog
			?? throw new InvalidOperationException($"{handler.GetType().Name} did not create an AlertDialog.");
	}

	static AppCompatAlertDialog CreateAndAssignWeakCallbackDialog(
		Context androidContext,
		Picker picker,
		IElementHandler handler,
		FieldInfo dialogField)
	{
		var weakHandler = new WeakReference<IElementHandler>(handler);
		var items = picker.Items.ToArray();

		using var builder = new MaterialAlertDialogBuilder(androidContext);
		builder.SetTitle(picker.Title ?? string.Empty);
		builder.SetSingleChoiceItems(items, picker.SelectedIndex, (sender, args) =>
		{
			if (weakHandler.TryGetTarget(out var target) && target.VirtualView is IPicker targetPicker)
				targetPicker.SelectedIndex = args.Which;
		});
		builder.SetNegativeButton(AResource.String.Cancel, static (sender, args) => { });

		var dialog = builder.Create();
		dialog.SetCanceledOnTouchOutside(true);
		dialog.Show();

		dialogField.SetValue(handler, dialog);
		return dialog;
	}

	static void Disconnect(IElement view, IElementHandler handler)
	{
		((IElementHandler)handler).DisconnectHandler();
		view.Handler = null;
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
		string HandlerKind,
		int Cycle,
		WeakReference<AppCompatAlertDialog> Dialog,
		WeakReference<IElement> VirtualPicker,
		WeakReference<IElementHandler> Handler,
		WeakReference<MauiContext> MauiContext,
		WeakReference<PayloadServiceProvider> ServiceProvider,
		WeakReference<byte[]> ContextPayload)
	{
		public static TrackedCycle Create(
			string handlerKind,
			int cycle,
			AppCompatAlertDialog dialog,
			IElement virtualPicker,
			IElementHandler handler,
			MauiContext mauiContext,
			PayloadServiceProvider serviceProvider)
		{
			return new TrackedCycle(
				handlerKind,
				cycle,
				new WeakReference<AppCompatAlertDialog>(dialog),
				new WeakReference<IElement>(virtualPicker),
				new WeakReference<IElementHandler>(handler),
				new WeakReference<MauiContext>(mauiContext),
				new WeakReference<PayloadServiceProvider>(serviceProvider),
				new WeakReference<byte[]>(serviceProvider.Payload));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveDialogs,
		int AliveVirtualPickers,
		int AliveHandlers,
		int AliveMauiContexts,
		int AliveServiceProviders,
		int AliveContextPayloads,
		long RetainedContextPayloadBytes,
		IReadOnlyDictionary<string, TypeResult> ByHandlerKind)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveDialogs = 0;
			var aliveVirtualPickers = 0;
			var aliveHandlers = 0;
			var aliveMauiContexts = 0;
			var aliveServiceProviders = 0;
			var aliveContextPayloads = 0;
			long retainedContextPayloadBytes = 0;
			var byKind = new Dictionary<string, TypeCounter>(StringComparer.Ordinal);

			foreach (var cycle in tracked)
			{
				var counter = GetCounter(byKind, cycle.HandlerKind);
				counter.Tracked++;

				if (cycle.Dialog.TryGetTarget(out _))
				{
					aliveDialogs++;
					counter.AliveDialogs++;
				}

				if (cycle.VirtualPicker.TryGetTarget(out _))
				{
					aliveVirtualPickers++;
					counter.AliveVirtualPickers++;
				}

				if (cycle.Handler.TryGetTarget(out _))
				{
					aliveHandlers++;
					counter.AliveHandlers++;
				}

				if (cycle.MauiContext.TryGetTarget(out _))
				{
					aliveMauiContexts++;
					counter.AliveMauiContexts++;
				}

				if (cycle.ServiceProvider.TryGetTarget(out _))
				{
					aliveServiceProviders++;
					counter.AliveServiceProviders++;
				}

				if (cycle.ContextPayload.TryGetTarget(out _))
				{
					aliveContextPayloads++;
					retainedContextPayloadBytes += ContextPayloadBytes;
					counter.AliveContextPayloads++;
					counter.RetainedContextPayloadBytes += ContextPayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveDialogs,
				aliveVirtualPickers,
				aliveHandlers,
				aliveMauiContexts,
				aliveServiceProviders,
				aliveContextPayloads,
				retainedContextPayloadBytes,
				byKind.ToDictionary(pair => pair.Key, pair => pair.Value.ToResult(), StringComparer.Ordinal));
		}

		static TypeCounter GetCounter(Dictionary<string, TypeCounter> values, string handlerKind)
		{
			if (!values.TryGetValue(handlerKind, out var counter))
			{
				counter = new TypeCounter();
				values.Add(handlerKind, counter);
			}

			return counter;
		}
	}

	internal sealed record TypeResult(
		int Tracked,
		int AliveDialogs,
		int AliveVirtualPickers,
		int AliveHandlers,
		int AliveMauiContexts,
		int AliveServiceProviders,
		int AliveContextPayloads,
		long RetainedContextPayloadBytes);

	sealed class TypeCounter
	{
		public int Tracked { get; set; }
		public int AliveDialogs { get; set; }
		public int AliveVirtualPickers { get; set; }
		public int AliveHandlers { get; set; }
		public int AliveMauiContexts { get; set; }
		public int AliveServiceProviders { get; set; }
		public int AliveContextPayloads { get; set; }
		public long RetainedContextPayloadBytes { get; set; }

		public TypeResult ToResult() =>
			new(
				Tracked,
				AliveDialogs,
				AliveVirtualPickers,
				AliveHandlers,
				AliveMauiContexts,
				AliveServiceProviders,
				AliveContextPayloads,
				RetainedContextPayloadBytes);
	}
}

internal sealed class PayloadServiceProvider : IServiceProvider
{
	readonly IServiceProvider _inner;

	public PayloadServiceProvider(IServiceProvider inner, string name, int payloadBytes)
	{
		_inner = inner;
		Name = name;
		Payload = new byte[payloadBytes];
		Array.Fill(Payload, (byte)(name.Length % 251));
	}

	public string Name { get; }

	public byte[] Payload { get; }

	public object? GetService(Type serviceType)
	{
		return _inner.GetService(serviceType);
	}
}

internal sealed record ReproReport(
	int CyclesPerHandlerType,
	int HandlerTypes,
	int ItemsPerPicker,
	int ContextPayloadBytes,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int TotalCycles => CyclesPerHandlerType * HandlerTypes;

	public bool LeakProved =>
		Control.AliveDialogs == TotalCycles &&
		Control.AliveHandlers == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AliveServiceProviders == 0 &&
		Control.AliveContextPayloads == 0 &&
		Current.AliveDialogs == TotalCycles &&
		Current.AliveHandlers == TotalCycles &&
		Current.AliveMauiContexts == TotalCycles &&
		Current.AliveServiceProviders == TotalCycles &&
		Current.AliveContextPayloads == TotalCycles;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidPickerDialogCallbackRetentionRepro",
			$"Cycles per picker handler type: {CyclesPerHandlerType}",
			$"Picker handler types per scenario: {HandlerTypes} (PickerHandler, PickerHandler2)",
			$"Total handler cycles per scenario: {TotalCycles}",
			$"Items per picker dialog: {ItemsPerPicker}",
			$"Context payload bytes per cycle: {ContextPayloadBytes:N0}",
			$"Expected current retained context payload: {FormatBytes((long)TotalCycles * ContextPayloadBytes)}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained context payload: {FormatBytes(Control.RetainedContextPayloadBytes)}",
			$"Current retained context payload: {FormatBytes(Current.RetainedContextPayloadBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var lines = new List<string>
		{
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native dialogs: {result.AliveDialogs}/{result.TrackedCycles}",
			$"  alive virtual pickers: {result.AliveVirtualPickers}/{result.TrackedCycles}",
			$"  alive picker handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive MauiContexts: {result.AliveMauiContexts}/{result.TrackedCycles}",
			$"  alive service providers: {result.AliveServiceProviders}/{result.TrackedCycles}",
			$"  alive context payload byte arrays: {result.AliveContextPayloads}/{result.TrackedCycles}",
			$"  retained context payload bytes: {result.RetainedContextPayloadBytes:N0}"
		};

		foreach (var pair in result.ByHandlerKind.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var value = pair.Value;
			lines.Add(
				$"  {pair.Key}: dialogs={value.AliveDialogs}/{value.Tracked}, virtualPickers={value.AliveVirtualPickers}/{value.Tracked}, handlers={value.AliveHandlers}/{value.Tracked}, contexts={value.AliveMauiContexts}/{value.Tracked}, providers={value.AliveServiceProviders}/{value.Tracked}, payloads={value.AliveContextPayloads}/{value.Tracked}, retained={value.RetainedContextPayloadBytes:N0}");
		}

		return string.Join(Environment.NewLine, lines);
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
