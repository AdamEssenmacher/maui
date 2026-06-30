#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;

namespace AndroidDateTimePickerDialogCallbackRetentionRepro;

internal static class ReproSession
{
	const int CyclesPerPickerKind = 48;
	const int ContextPayloadBytes = 512 * 1024;
	const int PickerKindsPerRun = 2;

	static readonly FieldInfo DateDialogField =
		typeof(DatePickerHandler).GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(DatePickerHandler).FullName, "_dialog");

	static readonly FieldInfo TimeDialogField =
		typeof(TimePickerHandler).GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(TimePickerHandler).FullName, "_dialog");

	static readonly MethodInfo DateCreateDialogMethod =
		typeof(DatePickerHandler).GetMethod("CreateDatePickerDialog", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(DatePickerHandler).FullName, "CreateDatePickerDialog");

	static readonly MethodInfo TimeCreateDialogMethod =
		typeof(TimePickerHandler).GetMethod("CreateTimePickerDialog", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(TimePickerHandler).FullName, "CreateTimePickerDialog");

	static readonly List<Dialog> RetainedDialogRoots = new();

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
			"control: retained native dialogs with weak date/time callbacks",
			useWeakCallbackHandlers: true);

		var current = await RunScenarioAsync(
			rootContext.Services,
			androidContext,
			"current: retained native dialogs keep constructor callbacks that capture disconnected handlers",
			useWeakCallbackHandlers: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedDialogRoots);

		return new ReproReport(
			CyclesPerPickerKind,
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
		bool useWeakCallbackHandlers)
	{
		var tracked = new List<TrackedCycle>(CyclesPerPickerKind * PickerKindsPerRun);

		for (var i = 0; i < CyclesPerPickerKind; i++)
		{
			CreateDateCycle(rootServices, androidContext, i, tracked, useWeakCallbackHandlers);
			CreateTimeCycle(rootServices, androidContext, i, tracked, useWeakCallbackHandlers);

			if (i % 8 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDateCycle(
		IServiceProvider rootServices,
		Context androidContext,
		int cycle,
		List<TrackedCycle> tracked,
		bool useWeakCallbackHandler)
	{
		var serviceProvider = new PayloadServiceProvider(rootServices, $"date-{cycle:D4}", ContextPayloadBytes);
		var cycleContext = new MauiContext(serviceProvider, androidContext);
		var picker = new DatePicker
		{
			Date = new DateTime(2026, 6, 30).AddDays(cycle % 24)
		};
		DatePickerHandler handler = useWeakCallbackHandler
			? new WeakCallbackDatePickerHandler()
			: new DatePickerHandler();

		AttachHandler(picker, handler, cycleContext);

		var date = picker.Date ?? DateTime.Today;
		if (DateCreateDialogMethod.Invoke(handler, new object[] { date.Year, date.Month - 1, date.Day }) is not DatePickerDialog dialog)
			throw new InvalidOperationException("DatePickerHandler did not create a DatePickerDialog.");

		DateDialogField.SetValue(handler, dialog);
		Disconnect(picker, handler);

		RetainedDialogRoots.Add(dialog);
		tracked.Add(TrackedCycle.Create("date", cycle, dialog, picker, handler, cycleContext, serviceProvider));

		serviceProvider = null!;
		cycleContext = null!;
		picker = null!;
		handler = null!;
		dialog = null!;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateTimeCycle(
		IServiceProvider rootServices,
		Context androidContext,
		int cycle,
		List<TrackedCycle> tracked,
		bool useWeakCallbackHandler)
	{
		var serviceProvider = new PayloadServiceProvider(rootServices, $"time-{cycle:D4}", ContextPayloadBytes);
		var cycleContext = new MauiContext(serviceProvider, androidContext);
		var picker = new TimePicker
		{
			Time = TimeSpan.FromMinutes((cycle * 17) % (24 * 60))
		};
		TimePickerHandler handler = useWeakCallbackHandler
			? new WeakCallbackTimePickerHandler()
			: new TimePickerHandler();

		AttachHandler(picker, handler, cycleContext);

		var time = picker.Time ?? TimeSpan.Zero;
		var hour = time.Hours;
		var minute = time.Minutes;
		if (TimeCreateDialogMethod.Invoke(handler, new object[] { hour, minute }) is not TimePickerDialog dialog)
			throw new InvalidOperationException("TimePickerHandler did not create a TimePickerDialog.");

		TimeDialogField.SetValue(handler, dialog);
		Disconnect(picker, handler);

		RetainedDialogRoots.Add(dialog);
		tracked.Add(TrackedCycle.Create("time", cycle, dialog, picker, handler, cycleContext, serviceProvider));

		serviceProvider = null!;
		cycleContext = null!;
		picker = null!;
		handler = null!;
		dialog = null!;
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

	sealed class WeakCallbackDatePickerHandler : DatePickerHandler
	{
		protected override DatePickerDialog CreateDatePickerDialog(int year, int month, int day)
		{
			var weakHandler = new WeakReference<WeakCallbackDatePickerHandler>(this);

			return new DatePickerDialog(Context!, (_, args) =>
			{
				if (weakHandler.TryGetTarget(out var handler) && handler.VirtualView is not null)
					handler.VirtualView.Date = args.Date;
			}, year, month, day);
		}
	}

	sealed class WeakCallbackTimePickerHandler : TimePickerHandler
	{
		protected override TimePickerDialog CreateTimePickerDialog(int hour, int minute)
		{
			var weakHandler = new WeakReference<WeakCallbackTimePickerHandler>(this);

			return new TimePickerDialog(Context!, (_, args) =>
			{
				if (weakHandler.TryGetTarget(out var handler) && handler.VirtualView is not null)
				{
					handler.VirtualView.Time = new TimeSpan(args.HourOfDay, args.Minute, 0);
					handler.VirtualView.IsFocused = false;
				}
			}, hour, minute, false);
		}
	}

	internal sealed record TrackedCycle(
		string Kind,
		int Cycle,
		WeakReference<Dialog> Dialog,
		WeakReference<IElement> VirtualPicker,
		WeakReference<IElementHandler> Handler,
		WeakReference<MauiContext> MauiContext,
		WeakReference<PayloadServiceProvider> ServiceProvider,
		WeakReference<byte[]> ContextPayload)
	{
		public static TrackedCycle Create(
			string kind,
			int cycle,
			Dialog dialog,
			IElement virtualPicker,
			IElementHandler handler,
			MauiContext mauiContext,
			PayloadServiceProvider serviceProvider)
		{
			return new TrackedCycle(
				kind,
				cycle,
				new WeakReference<Dialog>(dialog),
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
		int AliveDateDialogs,
		int AliveTimeDialogs,
		int AliveVirtualDatePickers,
		int AliveVirtualTimePickers,
		int AliveDatePickerHandlers,
		int AliveTimePickerHandlers,
		int AliveMauiContexts,
		int AliveServiceProviders,
		int AliveContextPayloads,
		long RetainedContextPayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveDateDialogs = 0;
			var aliveTimeDialogs = 0;
			var aliveVirtualDatePickers = 0;
			var aliveVirtualTimePickers = 0;
			var aliveDatePickerHandlers = 0;
			var aliveTimePickerHandlers = 0;
			var aliveMauiContexts = 0;
			var aliveServiceProviders = 0;
			var aliveContextPayloads = 0;
			long retainedContextPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Dialog.TryGetTarget(out _))
				{
					if (cycle.Kind == "date")
						aliveDateDialogs++;
					else
						aliveTimeDialogs++;
				}

				if (cycle.VirtualPicker.TryGetTarget(out _))
				{
					if (cycle.Kind == "date")
						aliveVirtualDatePickers++;
					else
						aliveVirtualTimePickers++;
				}

				if (cycle.Handler.TryGetTarget(out _))
				{
					if (cycle.Kind == "date")
						aliveDatePickerHandlers++;
					else
						aliveTimePickerHandlers++;
				}

				if (cycle.MauiContext.TryGetTarget(out _))
					aliveMauiContexts++;

				if (cycle.ServiceProvider.TryGetTarget(out _))
					aliveServiceProviders++;

				if (cycle.ContextPayload.TryGetTarget(out _))
				{
					aliveContextPayloads++;
					retainedContextPayloadBytes += ContextPayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveDateDialogs,
				aliveTimeDialogs,
				aliveVirtualDatePickers,
				aliveVirtualTimePickers,
				aliveDatePickerHandlers,
				aliveTimePickerHandlers,
				aliveMauiContexts,
				aliveServiceProviders,
				aliveContextPayloads,
				retainedContextPayloadBytes);
		}
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
	int CyclesPerPickerKind,
	int ContextPayloadBytes,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int ExpectedCyclesPerRun => CyclesPerPickerKind * 2;

	public bool LeakProved =>
		Control.AliveDateDialogs == CyclesPerPickerKind &&
		Control.AliveTimeDialogs == CyclesPerPickerKind &&
		Control.AliveDatePickerHandlers == 0 &&
		Control.AliveTimePickerHandlers == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AliveContextPayloads == 0 &&
		Current.AliveDateDialogs == CyclesPerPickerKind &&
		Current.AliveTimeDialogs == CyclesPerPickerKind &&
		Current.AliveDatePickerHandlers == CyclesPerPickerKind &&
		Current.AliveTimePickerHandlers == CyclesPerPickerKind &&
		Current.AliveMauiContexts == ExpectedCyclesPerRun &&
		Current.AliveServiceProviders == ExpectedCyclesPerRun &&
		Current.AliveContextPayloads == ExpectedCyclesPerRun;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidDateTimePickerDialogCallbackRetentionRepro",
			$"Cycles per picker kind: {CyclesPerPickerKind}",
			$"Picker kinds per run: 2 (DatePickerDialog, TimePickerDialog)",
			$"Context payload bytes per cycle: {ContextPayloadBytes:N0}",
			$"Expected current payload cycles: {ExpectedCyclesPerRun}",
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
		var cyclesPerKind = result.TrackedCycles / 2;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive DatePickerDialogs: {result.AliveDateDialogs}/{cyclesPerKind}",
			$"  alive TimePickerDialogs: {result.AliveTimeDialogs}/{cyclesPerKind}",
			$"  alive virtual DatePickers: {result.AliveVirtualDatePickers}/{cyclesPerKind}",
			$"  alive virtual TimePickers: {result.AliveVirtualTimePickers}/{cyclesPerKind}",
			$"  alive DatePickerHandlers: {result.AliveDatePickerHandlers}/{cyclesPerKind}",
			$"  alive TimePickerHandlers: {result.AliveTimePickerHandlers}/{cyclesPerKind}",
			$"  alive MauiContexts: {result.AliveMauiContexts}/{result.TrackedCycles}",
			$"  alive service providers: {result.AliveServiceProviders}/{result.TrackedCycles}",
			$"  alive context payload byte arrays: {result.AliveContextPayloads}/{result.TrackedCycles}",
			$"  retained context payload bytes: {result.RetainedContextPayloadBytes:N0}");
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
