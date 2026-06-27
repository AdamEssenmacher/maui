#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Maps.Handlers;

namespace AndroidMapReadyCallbackRetentionLeakRepro;

public static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly Type CallbackType =
		typeof(MapHandler).Assembly.GetType("Microsoft.Maui.Maps.Handlers.MapCallbackHandler", throwOnError: true)
		?? throw new InvalidOperationException("MapCallbackHandler type was not found.");

	static readonly FieldInfo MapReadyField =
		typeof(MapHandler).GetField("_mapReady", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("MapHandler._mapReady was not found.");

	public static async Task<ReproReport> RunAsync()
	{
		var control = await RunScenarioAsync("control: dispose pending MapCallbackHandler", disposePendingCallback: true);
		var current = await RunScenarioAsync("current: Disconnect drops field but leaves pending callback live", disposePendingCallback: false);

		return new ReproReport(control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool disposePendingCallback)
	{
		var nativePendingCallbacks = new List<Java.Lang.Object>(Attempts);
		var handlers = new List<WeakReference>(Attempts);
		var contexts = new List<WeakReference>(Attempts);
		var payloads = new List<WeakReference>(Attempts);
		var payloadArrays = new List<WeakReference>(Attempts);
		var callbacks = new List<WeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			var attempt = CreateAttempt(i, nativePendingCallbacks, disposePendingCallback);
			handlers.Add(attempt.Handler);
			contexts.Add(attempt.Context);
			payloads.Add(attempt.Payload);
			payloadArrays.Add(attempt.PayloadBytes);
			callbacks.Add(attempt.Callback);

			if (i % 10 == 0)
				await Task.Yield();
		}

		await ForceFullGcAsync();

		var result = new ScenarioResult(
			name,
			nativePendingCallbacks.Count,
			CountAlive(callbacks),
			CountAlive(handlers),
			CountAlive(contexts),
			CountAlive(payloads),
			CountAlive(payloadArrays));

		GC.KeepAlive(nativePendingCallbacks);
		return result;
	}

	static AttemptRefs CreateAttempt(int index, List<Java.Lang.Object> nativePendingCallbacks, bool disposePendingCallback)
	{
		var payload = new Payload(index, PayloadBytes);
		var serviceProvider = new PayloadServiceProvider(payload);
		var mauiContext = new MauiContext(serviceProvider);
		var handler = new MapHandler();
		handler.SetMauiContext(mauiContext);

		var callback = CreateMapReadyCallback(handler);
		MapReadyField.SetValue(handler, callback);
		nativePendingCallbacks.Add(callback);

		// This mirrors the current disconnect ending: MAUI drops its _mapReady field,
		// but a native MapView can still retain the callback until map readiness.
		MapReadyField.SetValue(handler, null);

		if (disposePendingCallback)
			callback.Dispose();

		var result = new AttemptRefs(
			new WeakReference(handler),
			new WeakReference(mauiContext),
			new WeakReference(payload),
			new WeakReference(payload.Bytes),
			new WeakReference(callback));

		callback = null!;
		handler = null!;
		mauiContext = null!;
		serviceProvider = null!;
		payload = null!;

		return result;
	}

	static Java.Lang.Object CreateMapReadyCallback(MapHandler handler)
	{
		return (Java.Lang.Object)(Activator.CreateInstance(
			CallbackType,
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			args: new object[] { handler },
			culture: null)
			?? throw new InvalidOperationException("Could not create MapCallbackHandler."));
	}

	static async Task ForceFullGcAsync()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			await Task.Delay(50);
		}
	}

	static int CountAlive(List<WeakReference> weakReferences)
	{
		var count = 0;
		foreach (var weakReference in weakReferences)
		{
			if (weakReference.IsAlive)
				count++;
		}

		return count;
	}

	sealed class PayloadServiceProvider : IServiceProvider
	{
		readonly Payload _payload;

		public PayloadServiceProvider(Payload payload)
		{
			_payload = payload;
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(Payload))
				return _payload;

			return null;
		}
	}

	sealed class Payload
	{
		public Payload(int index, int size)
		{
			Bytes = new byte[size];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)(index + i);
		}

		public byte[] Bytes { get; }
	}

	readonly record struct AttemptRefs(
		WeakReference Handler,
		WeakReference Context,
		WeakReference Payload,
		WeakReference PayloadBytes,
		WeakReference Callback);
}

public sealed record ScenarioResult(
	string Name,
	int NativePendingCallbacks,
	int CallbacksAlive,
	int HandlersAlive,
	int ContextsAlive,
	int PayloadsAlive,
	int PayloadByteArraysAlive);

public sealed record ReproReport(ScenarioResult Control, ScenarioResult Current)
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	public bool LeakProved =>
		Control.NativePendingCallbacks == Attempts &&
		Control.CallbacksAlive == Attempts &&
		Control.HandlersAlive == 0 &&
		Control.ContextsAlive == 0 &&
		Control.PayloadsAlive == 0 &&
		Control.PayloadByteArraysAlive == 0 &&
		Current.NativePendingCallbacks == Attempts &&
		Current.CallbacksAlive == Attempts &&
		Current.HandlersAlive == Attempts &&
		Current.ContextsAlive == Attempts &&
		Current.PayloadsAlive == Attempts &&
		Current.PayloadByteArraysAlive == Attempts;

	public string ToText()
	{
		var builder = new StringBuilder();
		builder.AppendLine("AndroidMapReadyCallbackRetentionLeakRepro");
		builder.AppendLine($"Attempts: {Attempts}");
		builder.AppendLine($"Payload per attempt: {FormatBytes(PayloadBytes)}");
		builder.AppendLine($"Leak proved: {LeakProved}");
		builder.AppendLine();
		AppendScenario(builder, Control);
		builder.AppendLine();
		AppendScenario(builder, Current);
		builder.AppendLine();
		builder.AppendLine($"Current retained payload bytes: {FormatBytes(Current.PayloadByteArraysAlive * (long)PayloadBytes)}");
		return builder.ToString();
	}

	static void AppendScenario(StringBuilder builder, ScenarioResult result)
	{
		builder.AppendLine($"Run: {result.Name}");
		builder.AppendLine($"  native-pending callbacks retained: {result.NativePendingCallbacks}/{Attempts}");
		builder.AppendLine($"  callbacks alive after full GC: {result.CallbacksAlive}/{Attempts}");
		builder.AppendLine($"  handlers alive after full GC: {result.HandlersAlive}/{Attempts}");
		builder.AppendLine($"  MauiContexts alive after full GC: {result.ContextsAlive}/{Attempts}");
		builder.AppendLine($"  payloads alive after full GC: {result.PayloadsAlive}/{Attempts}");
		builder.AppendLine($"  payload byte arrays alive after full GC: {result.PayloadByteArraysAlive}/{Attempts}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024.0 / 1024.0:0.0} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024.0:0.0} KiB";
		return $"{bytes} B";
	}
}
