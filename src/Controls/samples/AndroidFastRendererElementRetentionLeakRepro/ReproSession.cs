#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Android.Content;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Controls.Compatibility.Platform.Android.FastRenderers;
using Microsoft.Maui.Graphics;
using AView = Android.Views.View;
using ButtonRenderer = Microsoft.Maui.Controls.Compatibility.Platform.Android.FastRenderers.ButtonRenderer;
using FrameRenderer = Microsoft.Maui.Controls.Compatibility.Platform.Android.FastRenderers.FrameRenderer;
using ImageRenderer = Microsoft.Maui.Controls.Compatibility.Platform.Android.FastRenderers.ImageRenderer;
using LabelRenderer = Microsoft.Maui.Controls.Compatibility.Platform.Android.FastRenderers.LabelRenderer;

namespace AndroidFastRendererElementRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRenderers,
	int AliveElements,
	int AlivePayloads,
	int AlivePayloadByteArrays,
	long RetainedPayloadBytes,
	IReadOnlyDictionary<string, int> AlivePayloadsByRenderer);

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
			"AndroidFastRendererElementRetentionLeakRepro",
			$"Attempts: {Attempts} ({Attempts / ReproSession.RendererKinds.Length} per renderer kind)",
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
		var rendererBreakdown = string.Join(", ", stats.AlivePayloadsByRenderer.Select(static pair => $"{pair.Key}={pair.Value}"));

		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained disposed native renderers: {stats.Attempts}",
			$"  renderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}",
			$"  virtual elements alive after full GC: {stats.AliveElements}/{stats.Attempts}",
			$"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  alive payloads by renderer: {rendererBreakdown}",
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
	public static readonly string[] RendererKinds = ["LabelRenderer", "ButtonRenderer", "ImageRenderer", "FrameRenderer"];

	const int AttemptsPerRendererKind = 20;
	const int PayloadBytes = 1024 * 1024;
	const int Attempts = AttemptsPerRendererKind * 4;

	static readonly FieldInfo LabelElementField =
		typeof(LabelRenderer).GetField("_element", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(LabelRenderer), "_element");

	static readonly FieldInfo ButtonElementField =
		typeof(ButtonRenderer).GetField("_button", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(ButtonRenderer), "_button");

	static readonly FieldInfo ImageElementField =
		typeof(ImageRenderer).GetField("_element", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(ImageRenderer), "_element");

	static readonly FieldInfo FrameElementField =
		typeof(FrameRenderer).GetField("_element", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(FrameRenderer), "_element");

	static readonly FieldInfo LabelMotionEventHelperField =
		typeof(LabelRenderer).GetField("_motionEventHelper", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(LabelRenderer), "_motionEventHelper");

	static readonly FieldInfo ImageMotionEventHelperField =
		typeof(ImageRenderer).GetField("_motionEventHelper", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(ImageRenderer), "_motionEventHelper");

	static readonly FieldInfo FrameMotionEventHelperField =
		typeof(FrameRenderer).GetField("_motionEventHelper", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(FrameRenderer), "_motionEventHelper");

	static readonly FieldInfo MotionEventHelperElementField =
		LabelMotionEventHelperField.FieldType.GetField("_element", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(LabelMotionEventHelperField.FieldType.Name, "_element");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: dispose then clear stale FastRenderer element fields",
			clearRendererElementFields: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: disposed FastRenderers keep private virtual-element fields",
			clearRendererElementFields: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool clearRendererElementFields)
	{
		var retainedNativeRenderers = new List<AView>(Attempts);
		var rendererRefs = new List<WeakReference<AView>>(Attempts);
		var elementRefs = new List<WeakReference<VisualElement>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < AttemptsPerRendererKind; i++)
		{
			foreach (var rendererKind in RendererKinds)
			{
				CreateDisposedRenderer(
					mauiContext,
					rendererKind,
					clearRendererElementFields,
					retainedNativeRenderers,
					rendererRefs,
					elementRefs,
					payloadRefs,
					i);
			}

			if (i % 5 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedNativeRenderers);

		var aliveRenderers = rendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveElements = elementRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.WeakReference.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.BytesWeakReference.TryGetTarget(out _));
		var aliveByRenderer = RendererKinds.ToDictionary(
			static kind => kind,
			kind => payloadRefs.Count(payload => payload.RendererKind == kind && payload.WeakReference.TryGetTarget(out _)));

		return new RunStats(
			name,
			Attempts,
			aliveRenderers,
			aliveElements,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes,
			aliveByRenderer);
	}

	static void CreateDisposedRenderer(
		IMauiContext mauiContext,
		string rendererKind,
		bool clearRendererElementFields,
		List<AView> retainedNativeRenderers,
		List<WeakReference<AView>> rendererRefs,
		List<WeakReference<VisualElement>> elementRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new Payload($"{rendererKind} payload {index}", PayloadBytes);
		var element = CreateElement(rendererKind, payload, index);
		var renderer = CreateRenderer(rendererKind, mauiContext.Context ?? throw new InvalidOperationException("Android context is not available."));

		var contextHandler = new ContextOnlyViewHandler(mauiContext, renderer);
		contextHandler.SetVirtualView(element);
		((IElement)element).Handler = contextHandler;

		payloadRefs.Add(new PayloadWeakReference(rendererKind, new WeakReference<Payload>(payload), new WeakReference<byte[]>(payload.Bytes)));
		elementRefs.Add(new WeakReference<VisualElement>(element));
		rendererRefs.Add(new WeakReference<AView>(renderer));
		retainedNativeRenderers.Add(renderer);

		((IVisualElementRenderer)renderer).SetElement(element);
		Platform.SetRenderer(element, (IVisualElementRenderer)renderer);

		renderer.Dispose();

		if (clearRendererElementFields)
			ClearRendererElementFields(renderer);
	}

	static VisualElement CreateElement(string rendererKind, Payload payload, int index)
	{
		return rendererKind switch
		{
			"LabelRenderer" => new Label
			{
				Text = $"Inventory item {index}",
				BindingContext = payload
			},
			"ButtonRenderer" => new Button
			{
				Text = $"Submit order {index}",
				BindingContext = payload
			},
			"ImageRenderer" => new Image
			{
				AutomationId = $"catalog-image-{index}",
				BindingContext = payload
			},
			"FrameRenderer" => new Frame
			{
				Content = new Label { Text = $"Customer card {index}" },
				BindingContext = payload
			},
			_ => throw new ArgumentOutOfRangeException(nameof(rendererKind), rendererKind, null)
		};
	}

	static AView CreateRenderer(string rendererKind, Context context)
	{
		return rendererKind switch
		{
			"LabelRenderer" => new LabelRenderer(context),
			"ButtonRenderer" => new ButtonRenderer(context),
			"ImageRenderer" => new ImageRenderer(context),
			"FrameRenderer" => new FrameRenderer(context),
			_ => throw new ArgumentOutOfRangeException(nameof(rendererKind), rendererKind, null)
		};
	}

	static void ClearRendererElementFields(AView renderer)
	{
		switch (renderer)
		{
			case LabelRenderer labelRenderer:
				LabelElementField.SetValue(labelRenderer, null);
				ClearMotionEventHelperElement(LabelMotionEventHelperField.GetValue(labelRenderer));
				break;
			case ButtonRenderer buttonRenderer:
				ButtonElementField.SetValue(buttonRenderer, null);
				break;
			case ImageRenderer imageRenderer:
				ImageElementField.SetValue(imageRenderer, null);
				ClearMotionEventHelperElement(ImageMotionEventHelperField.GetValue(imageRenderer));
				break;
			case FrameRenderer frameRenderer:
				FrameElementField.SetValue(frameRenderer, null);
				ClearMotionEventHelperElement(FrameMotionEventHelperField.GetValue(frameRenderer));
				break;
		}
	}

	static void ClearMotionEventHelperElement(object? motionEventHelper)
	{
		if (motionEventHelper is not null)
			MotionEventHelperElementField.SetValue(motionEventHelper, null);
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

	sealed record PayloadWeakReference(string RendererKind, WeakReference<Payload> WeakReference, WeakReference<byte[]> BytesWeakReference);

	sealed class Payload
	{
		public Payload(string label, int byteCount)
		{
			Label = label;
			Bytes = new byte[byteCount];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)((label.Length + i) % 251);
			Bytes[^1] = (byte)((label.Length + Bytes.Length) % 251);
		}

		public string Label { get; }

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
