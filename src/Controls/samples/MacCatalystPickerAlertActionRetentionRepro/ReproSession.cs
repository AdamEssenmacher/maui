#nullable enable

using System.Reflection;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace MacCatalystPickerAlertActionRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 80;
	internal const int PayloadBytesPerContext = 1024 * 1024;

	static readonly List<RetainedAlert> RetainedNativeAlerts = new();
	static readonly FieldInfo MauiContextBackingField =
		typeof(ElementHandler).GetField("<MauiContext>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find ElementHandler.MauiContext backing field.");
	static readonly FieldInfo PickerControllerField =
		typeof(PickerHandler).GetField("_pickerController", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find PickerHandler._pickerController.");
	static readonly MethodInfo DisplayAlertMethod =
		typeof(PickerHandler).GetMethod("DisplayAlert", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find PickerHandler.DisplayAlert.");

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "maccatalyst-picker-alert-action-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext appContext)
	{
		WriteProgress("Starting Mac Catalyst PickerHandler alert action retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear disconnected picker handler MauiContext while retaining native alert actions",
			appContext,
			clearHandlerMauiContextAfterDisconnect: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: retain MAUI-created picker UIAlertActions that capture disconnected handlers",
			appContext,
			clearHandlerMauiContextAfterDisconnect: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeAlerts);

		return new ReproReport(
			Cycles,
			PayloadBytesPerContext,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext appContext,
		bool clearHandlerMauiContextAfterDisconnect)
	{
		var retainedAlerts = new List<RetainedAlert>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 10 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, appContext, clearHandlerMauiContextAfterDisconnect);
			retainedAlerts.Add(cycleResult.RetainedAlert);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativeAlerts.AddRange(retainedAlerts);
		ForceFullGc();

		return ScenarioResult.From(name, retainedAlerts, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext appContext,
		bool clearHandlerMauiContextAfterDisconnect)
	{
		var payloadContext = new PayloadMauiContext(appContext, cycle);
		var picker = new Picker
		{
			Title = $"Picker {cycle:000}"
		};

		picker.Items.Add($"Choice {cycle:000}");
		picker.SelectedIndex = 0;

		var handler = (PickerHandler)picker.ToHandler(payloadContext);
		var platformPicker = (MauiPicker)handler.PlatformView!;

		DisplayAlertMethod.Invoke(handler, new object[] { platformPicker, picker.SelectedIndex });

		var alert = (UIAlertController?)PickerControllerField.GetValue(handler)
			?? throw new InvalidOperationException("PickerHandler did not create a UIAlertController.");

		if (alert.Actions.Length != 1)
			throw new InvalidOperationException($"Expected one picker alert action, found {alert.Actions.Length}.");

		var retainedAlert = new RetainedAlert(alert);
		var tracked = TrackedCycle.Create(
			cycle,
			alert,
			payloadContext,
			payloadContext.Payload,
			picker,
			handler,
			platformPicker);

		((IElementHandler)handler).DisconnectHandler();

		if (clearHandlerMauiContextAfterDisconnect)
			ClearMauiContext(handler);

		await DrainMainQueueAsync();

		return new CycleResult(retainedAlert, tracked);
	}

	static void ClearMauiContext(IElementHandler handler)
	{
		MauiContextBackingField.SetValue(handler, null);
	}

	static async Task DrainMainQueueAsync()
	{
		await Task.Delay(20);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.02));
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(80);
		}
	}

	static void WriteProgress(string message)
	{
		try
		{
			File.WriteAllText(ResultsPath, message + Environment.NewLine);
		}
		catch
		{
			// Progress output is diagnostic only; the final report write remains authoritative.
		}
	}

	internal sealed class PayloadMauiContext : IMauiContext
	{
		readonly IMauiContext _innerContext;
		readonly PayloadServiceProvider _services;

		public PayloadMauiContext(IMauiContext innerContext, int cycle)
		{
			_innerContext = innerContext;
			Payload = new ContextPayload(cycle);
			_services = new PayloadServiceProvider(innerContext.Services);
		}

		public IServiceProvider Services => _services;

		public IMauiHandlersFactory Handlers => _innerContext.Handlers;

		public ContextPayload Payload { get; }
	}

#pragma warning disable CA1422 // Test-only detached window prevents presenting many native picker popovers.
	sealed class PayloadServiceProvider(IServiceProvider innerServices) : IServiceProvider
	{
		readonly UIWindow _detachedWindow = new();

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(UIWindow))
				return _detachedWindow;

			return innerServices.GetService(serviceType);
		}
	}
#pragma warning restore CA1422

	internal sealed class ContextPayload
	{
		readonly byte[] _bytes;

		public ContextPayload(int cycle)
		{
			_bytes = new byte[PayloadBytesPerContext];
			Array.Fill(_bytes, (byte)(cycle % 251));
		}

		public int Length => _bytes.Length;
	}

	internal sealed record RetainedAlert(UIAlertController Alert)
	{
		public int ActionCount => Alert.Actions.Length;
	}

	internal sealed record CycleResult(RetainedAlert RetainedAlert, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<UIAlertController> NativeAlert,
		WeakReference<PayloadMauiContext> PayloadContext,
		WeakReference<ContextPayload> Payload,
		WeakReference<Picker> Picker,
		WeakReference<IElementHandler> PickerHandler,
		WeakReference<MauiPicker> PlatformPicker)
	{
		public static TrackedCycle Create(
			int cycle,
			UIAlertController alert,
			PayloadMauiContext payloadContext,
			ContextPayload payload,
			Picker picker,
			IElementHandler handler,
			MauiPicker platformPicker)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<UIAlertController>(alert),
				new WeakReference<PayloadMauiContext>(payloadContext),
				new WeakReference<ContextPayload>(payload),
				new WeakReference<Picker>(picker),
				new WeakReference<IElementHandler>(handler),
				new WeakReference<MauiPicker>(platformPicker));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeAlerts,
		int RetainedNativeActions,
		int AliveNativeAlerts,
		int AlivePayloadContexts,
		int AlivePayloads,
		long EstimatedAlivePayloadBytes,
		int AlivePickers,
		int AlivePickerHandlers,
		int AlivePlatformPickers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedAlert> retainedAlerts,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeActions = retainedAlerts.Sum(alert => alert.ActionCount);
			var aliveNativeAlerts = 0;
			var alivePayloadContexts = 0;
			var alivePayloads = 0;
			long estimatedAlivePayloadBytes = 0;
			var alivePickers = 0;
			var alivePickerHandlers = 0;
			var alivePlatformPickers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeAlert.TryGetTarget(out _))
					aliveNativeAlerts++;

				if (cycle.PayloadContext.TryGetTarget(out _))
					alivePayloadContexts++;

				if (cycle.Payload.TryGetTarget(out var payload))
				{
					alivePayloads++;
					estimatedAlivePayloadBytes += payload.Length;
				}

				if (cycle.Picker.TryGetTarget(out _))
					alivePickers++;

				if (cycle.PickerHandler.TryGetTarget(out _))
					alivePickerHandlers++;

				if (cycle.PlatformPicker.TryGetTarget(out _))
					alivePlatformPickers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedAlerts.Count,
				retainedNativeActions,
				aliveNativeAlerts,
				alivePayloadContexts,
				alivePayloads,
				estimatedAlivePayloadBytes,
				alivePickers,
				alivePickerHandlers,
				alivePlatformPickers);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadBytesPerContext,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeAlerts == Cycles &&
		Control.RetainedNativeActions == Cycles &&
		Control.AlivePayloadContexts <= 1 &&
		Control.AlivePayloads <= 1 &&
		Control.AlivePickerHandlers >= Cycles &&
		Current.RetainedNativeAlerts == Cycles &&
		Current.RetainedNativeActions == Cycles &&
		Current.AlivePayloadContexts >= Cycles &&
		Current.AlivePayloads >= Cycles &&
		Current.EstimatedAlivePayloadBytes >= (long)Cycles * PayloadBytesPerContext &&
		Current.AlivePickerHandlers >= Cycles;

	public string ToText()
	{
		var currentPayloadMiB = Current.EstimatedAlivePayloadBytes / 1024d / 1024d;
		var controlPayloadMiB = Control.EstimatedAlivePayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"MacCatalystPickerAlertActionRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per throwaway MauiContext: {PayloadBytesPerContext:N0} bytes",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			"Note: the repro invokes PickerHandler.DisplayAlert with a detached UIWindow so the real UIAlertController/UIAlertAction graph is created without presenting 160 popovers.",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained context payload: {controlPayloadMiB:N1} MiB",
			$"Current estimated retained context payload: {currentPayloadMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var payloadMiB = result.EstimatedAlivePayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native alerts: {result.RetainedNativeAlerts}/{result.TrackedCycles}",
			$"  retained native actions: {result.RetainedNativeActions}/{result.TrackedCycles}",
			$"  alive native alerts: {result.AliveNativeAlerts}/{result.TrackedCycles}",
			$"  alive payload MauiContexts: {result.AlivePayloadContexts}/{result.TrackedCycles}",
			$"  alive payloads: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  estimated alive payload bytes: {result.EstimatedAlivePayloadBytes:N0}",
			$"  estimated alive payload MiB: {payloadMiB:N1}",
			$"  alive pickers: {result.AlivePickers}/{result.TrackedCycles}",
			$"  alive picker handlers: {result.AlivePickerHandlers}/{result.TrackedCycles}",
			$"  alive platform pickers: {result.AlivePlatformPickers}/{result.TrackedCycles}");
	}
}
