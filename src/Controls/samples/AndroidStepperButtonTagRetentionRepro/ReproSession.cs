#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AButton = Android.Widget.Button;

namespace AndroidStepperButtonTagRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 96;
	const int PayloadBytesPerContext = 1024 * 1024;

	static readonly List<AButton> RetainedNativeButtons = new();

	public static async Task<ReproReport> RunAsync(Activity activity)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear native button Tags and click listeners after handler disconnect",
			activity,
			clearButtonState: true);

		var current = await RunScenarioAsync(
			"current: StepperHandler disconnect leaves handler holders in native button Tags",
			activity,
			clearButtonState: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeButtons);

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
		Activity activity,
		bool clearButtonState)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateDisconnectedStepperCycle(activity, i, tracked, clearButtonState);

			if (i % 16 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateDisconnectedStepperCycle(
		Activity activity,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearButtonState)
	{
		var payload = new PayloadService(cycle, PayloadBytesPerContext);
		var services = new ServiceCollection()
			.AddSingleton(payload)
			.BuildServiceProvider();
		var context = new MauiContext(services, activity);

		var stepper = new Stepper
		{
			Minimum = 0,
			Maximum = 500,
			Increment = 5,
			Value = 125
		};

		var handler = new StepperHandler();
		handler.SetMauiContext(context);
		stepper.Handler = handler;

		var platformView = handler.PlatformView
			?? throw new InvalidOperationException("StepperHandler did not create an Android MauiStepper.");

		var downButton = platformView.GetChildAt(0) as AButton
			?? throw new InvalidOperationException("StepperHandler did not create a down Button child.");
		var upButton = platformView.GetChildAt(1) as AButton
			?? throw new InvalidOperationException("StepperHandler did not create an up Button child.");

		((IElementHandler)handler).DisconnectHandler();
		stepper.Handler = null;

		if (clearButtonState)
		{
			ClearNativeButtonState(downButton);
			ClearNativeButtonState(upButton);
		}

		RetainedNativeButtons.Add(downButton);
		RetainedNativeButtons.Add(upButton);
		tracked.Add(TrackedCycle.Create(cycle, downButton, upButton, stepper, handler, context, services, payload));
	}

	static void ClearNativeButtonState(AButton button)
	{
		button.Tag = null;
		button.SetOnClickListener(null);
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
		public PayloadService(int cycle, int payloadBytes)
		{
			Cycle = cycle;
			PayloadBytes = payloadBytes;
			Payload = CreatePayload(cycle, payloadBytes);
			Tokens = CreateTokens(cycle);
		}

		public int Cycle { get; }

		public int PayloadBytes { get; }

		public byte[] Payload { get; }

		public IReadOnlyList<string> Tokens { get; }
	}

	static string[] CreateTokens(int cycle)
	{
		var tokens = new string[16];
		for (var i = 0; i < tokens.Length; i++)
			tokens[i] = $"stepper-context-token-{cycle:D4}-{i:D2}";

		return tokens;
	}

	static byte[] CreatePayload(int cycle, int payloadBytes)
	{
		var payload = new byte[payloadBytes];
		for (var i = 0; i < payload.Length; i += 4096)
			payload[i] = (byte)(0x5A + cycle + i);

		return payload;
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<AButton> DownButton,
		WeakReference<AButton> UpButton,
		WeakReference<Stepper> Stepper,
		WeakReference<IElementHandler> Handler,
		WeakReference<MauiContext> MauiContext,
		WeakReference<IServiceProvider> ServiceProvider,
		WeakReference<PayloadService> PayloadService,
		WeakReference<byte[]> PayloadBytes,
		long PayloadBytesPerContext)
	{
		public static TrackedCycle Create(
			int cycle,
			AButton downButton,
			AButton upButton,
			Stepper stepper,
			IElementHandler handler,
			MauiContext context,
			IServiceProvider serviceProvider,
			PayloadService payload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<AButton>(downButton),
				new WeakReference<AButton>(upButton),
				new WeakReference<Stepper>(stepper),
				new WeakReference<IElementHandler>(handler),
				new WeakReference<MauiContext>(context),
				new WeakReference<IServiceProvider>(serviceProvider),
				new WeakReference<PayloadService>(payload),
				new WeakReference<byte[]>(payload.Payload),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveDownButtons,
		int AliveUpButtons,
		int AliveSteppers,
		int AliveHandlers,
		int AliveMauiContexts,
		int AliveServiceProviders,
		int AlivePayloadServices,
		int AlivePayloadByteArrays,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveDownButtons = 0;
			var aliveUpButtons = 0;
			var aliveSteppers = 0;
			var aliveHandlers = 0;
			var aliveMauiContexts = 0;
			var aliveServiceProviders = 0;
			var alivePayloadServices = 0;
			var alivePayloadByteArrays = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.DownButton.TryGetTarget(out _))
					aliveDownButtons++;

				if (cycle.UpButton.TryGetTarget(out _))
					aliveUpButtons++;

				if (cycle.Stepper.TryGetTarget(out _))
					aliveSteppers++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;

				if (cycle.MauiContext.TryGetTarget(out _))
					aliveMauiContexts++;

				if (cycle.ServiceProvider.TryGetTarget(out _))
					aliveServiceProviders++;

				if (cycle.PayloadService.TryGetTarget(out _))
					alivePayloadServices++;

				if (cycle.PayloadBytes.TryGetTarget(out _))
				{
					alivePayloadByteArrays++;
					retainedPayloadBytes += cycle.PayloadBytesPerContext;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveDownButtons,
				aliveUpButtons,
				aliveSteppers,
				aliveHandlers,
				aliveMauiContexts,
				aliveServiceProviders,
				alivePayloadServices,
				alivePayloadByteArrays,
				retainedPayloadBytes);
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
		Control.AliveDownButtons == Cycles &&
		Control.AliveUpButtons == Cycles &&
		Current.AliveDownButtons == Cycles &&
		Current.AliveUpButtons == Cycles &&
		Control.AliveHandlers == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveHandlers == Cycles &&
		Current.AliveMauiContexts == Cycles &&
		Current.AlivePayloadByteArrays == Cycles &&
		Current.AliveSteppers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidStepperButtonTagRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload bytes per MauiContext service graph: {PayloadBytesPerContext:N0}",
			"Source paths mirrored: StepperHandler.CreatePlatformView, StepperHandlerManager.CreateStepperButtons, native Button.Tag StepperHandlerHolder, and ElementHandler disconnect",
			"Retained peers: native Android Stepper Button children only",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained context payload: {controlMiB:N1} MiB",
			$"Current retained context payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native down buttons: {result.AliveDownButtons}/{result.TrackedCycles}",
			$"  alive native up buttons: {result.AliveUpButtons}/{result.TrackedCycles}",
			$"  alive Steppers: {result.AliveSteppers}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive MauiContexts: {result.AliveMauiContexts}/{result.TrackedCycles}",
			$"  alive service providers: {result.AliveServiceProviders}/{result.TrackedCycles}",
			$"  alive payload services: {result.AlivePayloadServices}/{result.TrackedCycles}",
			$"  alive payload byte arrays: {result.AlivePayloadByteArrays}/{result.TrackedCycles}",
			$"  retained payload bytes: {result.RetainedPayloadBytes:N0}");
	}
}
