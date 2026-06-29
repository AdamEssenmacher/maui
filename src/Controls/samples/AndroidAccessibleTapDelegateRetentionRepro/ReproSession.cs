#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using AndroidX.Core.View;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers;
using Microsoft.Maui.Controls.Platform;
using Microsoft.Maui.Handlers;
using AView = Android.Views.View;

namespace AndroidAccessibleTapDelegateRetentionRepro;

internal static class ReproSession
{
	public const int Cycles = 96;
	public const int PayloadBytesPerCycle = 1024 * 1024;

	static readonly List<AView> RetainedNativeViews = new();

	public static Task<ReproReport> RunAsync(IMauiContext rootContext)
	{
		RetainedNativeViews.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunScenario(
			rootContext,
			"control: clear native ControlsAccessibilityDelegate after handler disconnect",
			clearAccessibilityDelegate: true);

		var current = RunScenario(
			rootContext,
			"current: ViewHandler disconnect leaves ControlsAccessibilityDelegate assigned",
			clearAccessibilityDelegate: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeViews);

		return Task.FromResult(new ReproReport(
			Cycles,
			PayloadBytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current));
	}

	static ScenarioResult RunScenario(
		IMauiContext rootContext,
		string name,
		bool clearAccessibilityDelegate)
	{
		var androidContext = rootContext.Context
			?? Android.App.Application.Context
			?? throw new InvalidOperationException("No Android context is available.");
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
			CreateCycle(rootContext, androidContext, i, tracked, clearAccessibilityDelegate);

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateCycle(
		IMauiContext rootContext,
		Context androidContext,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearAccessibilityDelegate)
	{
		var payload = new PayloadService(cycle, PayloadBytesPerCycle);
		var services = new PayloadServiceProvider(rootContext.Services, payload);
		var cycleContext = new MauiContext(services, androidContext);
		var view = new BoxView
		{
			WidthRequest = 12,
			HeightRequest = 12
		};

		view.GestureRecognizers.Add(new TapGestureRecognizer
		{
			NumberOfTapsRequired = 1,
			Buttons = ButtonsMask.Primary,
			Command = new Command(static () => { })
		});

		var handler = new BoxViewHandler();
		handler.SetMauiContext(cycleContext);
		handler.SetVirtualView(view);

		if (handler.PlatformView is not AView platformView)
			throw new InvalidOperationException("BoxViewHandler did not create an Android platform view.");

		if (ViewCompat.GetAccessibilityDelegate(platformView) is not ControlsAccessibilityDelegate)
			throw new InvalidOperationException("The accessible tap gesture did not install ControlsAccessibilityDelegate.");

		((IElementHandler)handler).DisconnectHandler();

		if (clearAccessibilityDelegate)
			ViewCompat.SetAccessibilityDelegate(platformView, null);

		RetainedNativeViews.Add(platformView);
		tracked.Add(TrackedCycle.Create(platformView, handler, view, cycleContext, services, payload));

		payload = null!;
		services = null!;
		cycleContext = null!;
		view = null!;
		handler = null!;
		platformView = null!;
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

	internal sealed class PayloadService
	{
		public PayloadService(int cycle, int size)
		{
			Cycle = cycle;
			Payload = new byte[size];
			Payload[0] = (byte)(cycle % 251);
			Payload[^1] = (byte)((cycle + 97) % 251);
		}

		public int Cycle { get; }

		public byte[] Payload { get; }
	}

	internal sealed class PayloadServiceProvider : IServiceProvider
	{
		readonly IServiceProvider _inner;
		readonly PayloadService _payload;

		public PayloadServiceProvider(IServiceProvider inner, PayloadService payload)
		{
			_inner = inner;
			_payload = payload;
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(PayloadService))
				return _payload;

			return _inner.GetService(serviceType);
		}
	}

	internal sealed class TrackedCycle
	{
		readonly WeakReference<AView> _nativeView;
		readonly WeakReference<BoxViewHandler> _handler;
		readonly WeakReference<BoxView> _virtualView;
		readonly WeakReference<MauiContext> _mauiContext;
		readonly WeakReference<PayloadServiceProvider> _services;
		readonly WeakReference<PayloadService> _payloadService;
		readonly WeakReference<byte[]> _payload;

		TrackedCycle(
			AView nativeView,
			BoxViewHandler handler,
			BoxView virtualView,
			MauiContext mauiContext,
			PayloadServiceProvider services,
			PayloadService payloadService)
		{
			_nativeView = new WeakReference<AView>(nativeView);
			_handler = new WeakReference<BoxViewHandler>(handler);
			_virtualView = new WeakReference<BoxView>(virtualView);
			_mauiContext = new WeakReference<MauiContext>(mauiContext);
			_services = new WeakReference<PayloadServiceProvider>(services);
			_payloadService = new WeakReference<PayloadService>(payloadService);
			_payload = new WeakReference<byte[]>(payloadService.Payload);
		}

		public static TrackedCycle Create(
			AView nativeView,
			BoxViewHandler handler,
			BoxView virtualView,
			MauiContext mauiContext,
			PayloadServiceProvider services,
			PayloadService payloadService) =>
			new(nativeView, handler, virtualView, mauiContext, services, payloadService);

		public bool NativeViewAlive => _nativeView.TryGetTarget(out _);

		public bool HasControlsAccessibilityDelegate =>
			_nativeView.TryGetTarget(out var nativeView) &&
			ViewCompat.GetAccessibilityDelegate(nativeView) is ControlsAccessibilityDelegate;

		public bool HandlerAlive => _handler.TryGetTarget(out _);

		public bool VirtualViewAlive => _virtualView.TryGetTarget(out _);

		public bool MauiContextAlive => _mauiContext.TryGetTarget(out _);

		public bool ServicesAlive => _services.TryGetTarget(out _);

		public bool PayloadServiceAlive => _payloadService.TryGetTarget(out _);

		public bool PayloadAlive => _payload.TryGetTarget(out _);
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeViews,
		int AssignedControlsAccessibilityDelegates,
		int AliveHandlers,
		int AliveVirtualViews,
		int AliveMauiContexts,
		int AliveServiceProviders,
		int AlivePayloadServices,
		int AlivePayloads,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeViews = 0;
			var assignedDelegates = 0;
			var aliveHandlers = 0;
			var aliveVirtualViews = 0;
			var aliveMauiContexts = 0;
			var aliveServiceProviders = 0;
			var alivePayloadServices = 0;
			var alivePayloads = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeViewAlive)
					aliveNativeViews++;

				if (cycle.HasControlsAccessibilityDelegate)
					assignedDelegates++;

				if (cycle.HandlerAlive)
					aliveHandlers++;

				if (cycle.VirtualViewAlive)
					aliveVirtualViews++;

				if (cycle.MauiContextAlive)
					aliveMauiContexts++;

				if (cycle.ServicesAlive)
					aliveServiceProviders++;

				if (cycle.PayloadServiceAlive)
					alivePayloadServices++;

				if (cycle.PayloadAlive)
					alivePayloads++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeViews,
				assignedDelegates,
				aliveHandlers,
				aliveVirtualViews,
				aliveMauiContexts,
				aliveServiceProviders,
				alivePayloadServices,
				alivePayloads,
				(long)alivePayloads * PayloadBytesPerCycle);
		}

		public void AppendTo(StringBuilder builder)
		{
			builder.AppendLine($"Run: {Name}");
			builder.AppendLine($"  tracked cycles: {TrackedCycles}");
			builder.AppendLine($"  alive native views: {AliveNativeViews}/{TrackedCycles}");
			builder.AppendLine($"  assigned ControlsAccessibilityDelegates: {AssignedControlsAccessibilityDelegates}/{TrackedCycles}");
			builder.AppendLine($"  alive handlers: {AliveHandlers}/{TrackedCycles}");
			builder.AppendLine($"  alive virtual views: {AliveVirtualViews}/{TrackedCycles}");
			builder.AppendLine($"  alive MauiContexts: {AliveMauiContexts}/{TrackedCycles}");
			builder.AppendLine($"  alive service providers: {AliveServiceProviders}/{TrackedCycles}");
			builder.AppendLine($"  alive payload services: {AlivePayloadServices}/{TrackedCycles}");
			builder.AppendLine($"  alive payload byte arrays: {AlivePayloads}/{TrackedCycles}");
			builder.AppendLine($"  retained payload bytes: {RetainedPayloadBytes:N0}");
			builder.AppendLine();
		}
	}

	internal sealed record ReproReport(
		int Cycles,
		int PayloadBytesPerCycleValue,
		long BaselineManagedHeapBytes,
		long FinalManagedHeapBytes,
		ScenarioResult Control,
		ScenarioResult Current)
	{
		public bool LeakProved =>
			Control.AssignedControlsAccessibilityDelegates == 0 &&
			Control.AliveHandlers == 0 &&
			Control.AliveMauiContexts == 0 &&
			Control.AlivePayloads == 0 &&
			Current.AssignedControlsAccessibilityDelegates == Cycles &&
			Current.AliveHandlers == Cycles &&
			Current.AliveMauiContexts == Cycles &&
			Current.AlivePayloads == Cycles;

		public string ToText()
		{
			var builder = new StringBuilder();
			builder.AppendLine("AndroidAccessibleTapDelegateRetentionRepro");
			builder.AppendLine($"Cycles: {Cycles}");
			builder.AppendLine($"Payload bytes per cycle: {PayloadBytesPerCycleValue:N0}");
			builder.AppendLine($"Baseline managed heap: {BaselineManagedHeapBytes:N0} bytes");
			builder.AppendLine($"Final managed heap: {FinalManagedHeapBytes:N0} bytes");
			builder.AppendLine($"Managed heap delta: {FormatBytes(FinalManagedHeapBytes - BaselineManagedHeapBytes)}");
			builder.AppendLine($"Leak proved: {LeakProved}");
			builder.AppendLine();
			Control.AppendTo(builder);
			Current.AppendTo(builder);
			builder.AppendLine($"Control retained payload: {FormatBytes(Control.RetainedPayloadBytes)}");
			builder.AppendLine($"Current retained payload: {FormatBytes(Current.RetainedPayloadBytes)}");
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			return builder.ToString();
		}

		static string FormatBytes(long bytes)
		{
			var sign = bytes < 0 ? "-" : string.Empty;
			var value = Math.Abs(bytes);
			return $"{sign}{value / 1024d / 1024d:N1} MiB";
		}
	}
}
