#nullable enable
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Android.Content;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Graphics;
using AView = Android.Views.View;

namespace AndroidHandlerToRendererShimRendererPropertyRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRetainedElements,
	int AliveShims,
	int AliveHandlers,
	int AlivePayloads,
	int AlivePayloadByteArrays,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveRetainedElements == Attempts &&
		Control.AliveShims == 0 &&
		Control.AliveHandlers == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveRetainedElements == Attempts &&
		Current.AliveShims == Attempts &&
		Current.AliveHandlers == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadByteArrays == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidHandlerToRendererShimRendererPropertyRetentionLeakRepro",
			$"Attempts: {Attempts}",
			$"Handler payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  app-retained removed elements: {stats.AliveRetainedElements}/{stats.Attempts}",
			$"  disposed shims alive after full GC: {stats.AliveShims}/{stats.Attempts}",
			$"  disconnected handlers alive after full GC: {stats.AliveHandlers}/{stats.Attempts}",
			$"  handler payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  handler payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  retained handler payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : "";
		var value = Math.Abs((double)bytes);
		if (value >= 1024 * 1024)
			return $"{sign}{value / 1024 / 1024:0.0} MiB";
		if (value >= 1024)
			return $"{sign}{value / 1024:0.0} KiB";
		return $"{bytes} B";
	}
}

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo ShimElementField =
		typeof(HandlerToRendererShim).GetField("<Element>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(HandlerToRendererShim), "<Element>k__BackingField");

	static readonly FieldInfo ShimTrackerField =
		typeof(HandlerToRendererShim).GetField("<Tracker>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(HandlerToRendererShim), "<Tracker>k__BackingField");

	static readonly MethodInfo ShimElementPropertyChangedMethod =
		typeof(HandlerToRendererShim).GetMethod("OnElementPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(HandlerToRendererShim), "OnElementPropertyChanged");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: dispose shim then clear RendererProperty and stale fields",
			clearRendererPropertyAndShimFields: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: disposed shim remains in RendererProperty",
			clearRendererPropertyAndShimFields: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool clearRendererPropertyAndShimFields)
	{
		var retainedRemovedElements = new List<Label>(Attempts);
		var elementRefs = new List<WeakReference<Label>>(Attempts);
		var shimRefs = new List<WeakReference<HandlerToRendererShim>>(Attempts);
		var handlerRefs = new List<WeakReference<PayloadViewHandler>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedShim(
				mauiContext,
				clearRendererPropertyAndShimFields,
				retainedRemovedElements,
				elementRefs,
				shimRefs,
				handlerRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedRemovedElements);

		var aliveElements = elementRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveShims = shimRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveHandlers = handlerRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			aliveElements,
			aliveShims,
			aliveHandlers,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisposedShim(
		IMauiContext mauiContext,
		bool clearRendererPropertyAndShimFields,
		List<Label> retainedRemovedElements,
		List<WeakReference<Label>> elementRefs,
		List<WeakReference<HandlerToRendererShim>> shimRefs,
		List<WeakReference<PayloadViewHandler>> handlerRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var element = new Label
		{
			Text = $"Removed compatibility child {index}"
		};

		var handler = new PayloadViewHandler(
			mauiContext,
			mauiContext.Context ?? throw new InvalidOperationException("Android context is not available."),
			payload);
		var shim = new HandlerToRendererShim(handler);

		retainedRemovedElements.Add(element);
		elementRefs.Add(new WeakReference<Label>(element));
		shimRefs.Add(new WeakReference<HandlerToRendererShim>(shim));
		handlerRefs.Add(new WeakReference<PayloadViewHandler>(handler));
		payloadRefs.Add(new PayloadWeakReference(new WeakReference<Payload>(payload), new WeakReference<byte[]>(payload.Bytes)));

		shim.SetElement(element);
		Platform.SetRenderer(element, shim);
		shim.Dispose();

		if (clearRendererPropertyAndShimFields)
		{
			if (element.Handler == handler)
				element.Handler = null;

			var propertyChangedHandler = (PropertyChangedEventHandler)Delegate.CreateDelegate(
				typeof(PropertyChangedEventHandler),
				shim,
				ShimElementPropertyChangedMethod);
			element.PropertyChanged -= propertyChangedHandler;

			Platform.SetRenderer(element, null);
			(ShimTrackerField.GetValue(shim) as IDisposable)?.Dispose();
			ShimElementField.SetValue(shim, null);
			ShimTrackerField.SetValue(shim, null);
		}
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	sealed record PayloadWeakReference(WeakReference<Payload> Payload, WeakReference<byte[]> Bytes);

	sealed class Payload
	{
		public Payload(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)((id + i) % 251);
			Bytes[^1] = (byte)((id + Bytes.Length) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}

	sealed class PayloadViewHandler : IPlatformViewHandler
	{
		readonly TextView _platformView;
		readonly Payload _payload;

		public PayloadViewHandler(IMauiContext mauiContext, Context context, Payload payload)
		{
			MauiContext = mauiContext;
			_payload = payload;
			_platformView = new TextView(context)
			{
				Text = $"handler payload {_payload.Id}"
			};
		}

		public AView PlatformView => _platformView;

		public IView? VirtualView { get; private set; }

		object? IElementHandler.PlatformView => PlatformView;

		IElement? IElementHandler.VirtualView => VirtualView;

		public IMauiContext? MauiContext { get; private set; }

		public bool HasContainer { get; set; }

		public AView? ContainerView => null;

		object? IViewHandler.ContainerView => ContainerView;

		public void SetMauiContext(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public void SetVirtualView(IElement view)
		{
			VirtualView = (IView)view;
		}

		public void UpdateValue(string property)
		{
		}

		public void Invoke(string command, object? args = null)
		{
		}

		public void DisconnectHandler()
		{
			if (VirtualView?.Handler == this)
				VirtualView.Handler = null;

			VirtualView = null;
		}

		public Size GetDesiredSize(double widthConstraint, double heightConstraint)
		{
			return Size.Zero;
		}

		public void PlatformArrange(Rect frame)
		{
		}
	}
}
