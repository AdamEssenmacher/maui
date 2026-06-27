#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Graphics;
using AView = Android.Views.View;

namespace AndroidIndicatorViewRendererElementRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRenderers,
	int AliveElements,
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
		Control.AliveElements == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveElements == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadByteArrays == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidIndicatorViewRendererElementRetentionLeakRepro",
			$"Attempts: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
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
			$"  retained disposed native renderers: {stats.Attempts}",
			$"  renderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}",
			$"  IndicatorViews alive after full GC: {stats.AliveElements}/{stats.Attempts}",
			$"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
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

	static readonly FieldInfo IndicatorViewRendererIndicatorViewField =
		typeof(IndicatorViewRenderer).GetField("IndicatorView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(IndicatorViewRenderer), "IndicatorView");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: dispose then clear IndicatorViewRenderer.IndicatorView",
			clearRendererElement: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: disposed IndicatorViewRenderer keeps IndicatorView",
			clearRendererElement: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool clearRendererElement)
	{
		var retainedNativeRenderers = new List<AView>(Attempts);
		var rendererRefs = new List<WeakReference<IndicatorViewRenderer>>(Attempts);
		var elementRefs = new List<WeakReference<IndicatorView>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedRenderer(
				mauiContext,
				clearRendererElement,
				retainedNativeRenderers,
				rendererRefs,
				elementRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedNativeRenderers);

		var aliveRenderers = rendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveElements = elementRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			aliveRenderers,
			aliveElements,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisposedRenderer(
		IMauiContext mauiContext,
		bool clearRendererElement,
		List<AView> retainedNativeRenderers,
		List<WeakReference<IndicatorViewRenderer>> rendererRefs,
		List<WeakReference<IndicatorView>> elementRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var element = new IndicatorView
		{
			Count = 12,
			MaximumVisible = 6,
			Position = index % 6,
			HideSingle = false,
			IndicatorColor = Colors.LightGrey,
			SelectedIndicatorColor = Colors.Black,
			IndicatorSize = 8,
			BindingContext = payload
		};

		var renderer = new IndicatorViewRenderer(mauiContext.Context ?? throw new InvalidOperationException("Android context is not available."));
		var contextHandler = new ContextOnlyViewHandler(mauiContext, renderer);
		contextHandler.SetVirtualView(element);
		((IElement)element).Handler = contextHandler;

		payloadRefs.Add(new PayloadWeakReference(new WeakReference<Payload>(payload), new WeakReference<byte[]>(payload.Bytes)));
		elementRefs.Add(new WeakReference<IndicatorView>(element));
		rendererRefs.Add(new WeakReference<IndicatorViewRenderer>(renderer));
		retainedNativeRenderers.Add(renderer);

		((IVisualElementRenderer)renderer).SetElement(element);
		Platform.SetRenderer(element, renderer);

		renderer.Dispose();

		if (clearRendererElement)
			IndicatorViewRendererIndicatorViewField.SetValue(renderer, null);
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

	sealed class ContextOnlyViewHandler : IViewHandler
	{
		readonly AView _platformView;

		public ContextOnlyViewHandler(IMauiContext mauiContext, AView platformView)
		{
			MauiContext = mauiContext;
			_platformView = platformView;
		}

		public object? PlatformView => _platformView;

		public IView? VirtualView { get; private set; }

		IElement? IElementHandler.VirtualView => VirtualView;

		public IMauiContext? MauiContext { get; private set; }

		public bool HasContainer { get; set; }

		public object? ContainerView => null;

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
