#nullable enable

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using UIKit;

namespace IosLabelRendererFormattedStringRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadMegabytesPerCycle = 1;
	internal const int LabelWidthPoints = 420;
	internal const int LabelHeightPoints = 84;

	static readonly FieldInfo FormattedField =
		typeof(LabelRenderer).GetField("_formatted", BindingFlags.NonPublic | BindingFlags.Instance)
		?? throw new InvalidOperationException("Could not find LabelRenderer._formatted.");

	static readonly List<IReadOnlyList<LabelRenderer>> RetainedRendererPeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-labelrenderer-formattedstring-retention-results.txt");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		WriteProgress("Starting iOS LabelRenderer formatted string retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			mauiContext,
			"control: dispose renderer and clear private _formatted field",
			clearFormattedAfterDispose: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			mauiContext,
			"current: LabelRenderer dispose leaves private _formatted assigned",
			clearFormattedAfterDispose: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedRendererPeers);

		return new ReproReport(Cycles, PayloadMegabytesPerCycle, baselineBytes, finalBytes, control, current);
	}

	static ScenarioResult RunScenario(
		IMauiContext mauiContext,
		string name,
		bool clearFormattedAfterDispose)
	{
		var tracking = RunScenarioCore(mauiContext, name, clearFormattedAfterDispose);
		RetainedRendererPeers.Add(tracking.Renderers);
		ForceFullGc();

		return ScenarioResult.From(name, tracking.Renderers, tracking.TrackedCycles);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioTracking RunScenarioCore(
		IMauiContext mauiContext,
		string name,
		bool clearFormattedAfterDispose)
	{
		var renderers = new List<LabelRenderer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 12 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateDisposedRendererCycle(i, mauiContext, renderers, tracked, clearFormattedAfterDispose);
		}

		return new ScenarioTracking(renderers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisposedRendererCycle(
		int cycle,
		IMauiContext mauiContext,
		List<LabelRenderer> renderers,
		List<TrackedCycle> tracked,
		bool clearFormattedAfterDispose)
	{
		var payload = new DisclosurePayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var formatted = new PayloadFormattedString(payload);
		formatted.Spans.Add(new Span
		{
			Text = $"Order {cycle + 1:0000}: ",
			FontAttributes = FontAttributes.Bold
		});
		formatted.Spans.Add(new Span
		{
			Text = "review customer notices, consent copy, and item-level fulfillment constraints."
		});

		var label = new Label
		{
			AutomationId = $"labelrenderer-formatted-retention-{cycle + 1}",
			FormattedText = formatted,
			LineBreakMode = LineBreakMode.WordWrap,
			MaxLines = 3,
			WidthRequest = LabelWidthPoints,
			HeightRequest = LabelHeightPoints,
			BindingContext = payload
		};

		var contextHandler = new ContextOnlyElementHandler(mauiContext);
		label.Handler = contextHandler;

		var renderer = new LabelRenderer();
		SetRealisticBounds(renderer);
		renderer.SetElement(label);
		renderer.LayoutSubviews();

		if (GetFormatted(renderer) is not PayloadFormattedString)
			throw new InvalidOperationException("LabelRenderer did not cache the payload formatted string.");

		renderer.Dispose();
		label.FormattedText = null;
		label.BindingContext = null;
		label.Handler = null;
		contextHandler.DisconnectHandler();

		if (clearFormattedAfterDispose)
			ClearFormatted(renderer);

		renderers.Add(renderer);
		tracked.Add(TrackedCycle.Create(cycle, renderer, label, formatted, payload));
	}

	static void SetRealisticBounds(LabelRenderer renderer)
	{
		var bounds = new CGRect(0, 0, LabelWidthPoints, LabelHeightPoints);
		renderer.Frame = bounds;
		renderer.Bounds = bounds;
	}

	static FormattedString? GetFormatted(LabelRenderer renderer) =>
		(FormattedString?)FormattedField.GetValue(renderer);

	static void ClearFormatted(LabelRenderer renderer) =>
		FormattedField.SetValue(renderer, null);

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(100);
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

	internal sealed record ScenarioTracking(
		IReadOnlyList<LabelRenderer> Renderers,
		IReadOnlyList<TrackedCycle> TrackedCycles);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<LabelRenderer> Renderer,
		WeakReference<Label> Label,
		WeakReference<PayloadFormattedString> FormattedString,
		WeakReference<DisclosurePayload> Payload,
		long PayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			LabelRenderer renderer,
			Label label,
			PayloadFormattedString formattedString,
			DisclosurePayload payload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<LabelRenderer>(renderer),
				new WeakReference<Label>(label),
				new WeakReference<PayloadFormattedString>(formattedString),
				new WeakReference<DisclosurePayload>(payload),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedRendererPeers,
		int RenderersWithFormattedString,
		int AliveRenderers,
		int AliveLabels,
		int AliveFormattedStrings,
		int AlivePayloads,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<LabelRenderer> renderers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var renderersWithFormattedString = 0;
			foreach (var renderer in renderers)
			{
				if (GetFormatted(renderer) is PayloadFormattedString)
					renderersWithFormattedString++;
			}

			var aliveRenderers = 0;
			var aliveLabels = 0;
			var aliveFormattedStrings = 0;
			var alivePayloads = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.Label.TryGetTarget(out _))
					aliveLabels++;

				if (cycle.FormattedString.TryGetTarget(out _))
					aliveFormattedStrings++;

				if (cycle.Payload.TryGetTarget(out _))
				{
					alivePayloads++;
					retainedPayloadBytes += cycle.PayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				renderers.Count,
				renderersWithFormattedString,
				aliveRenderers,
				aliveLabels,
				aliveFormattedStrings,
				alivePayloads,
				retainedPayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadMegabytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedRendererPeers == Cycles &&
		Control.AliveRenderers == Cycles &&
		Control.RenderersWithFormattedString == 0 &&
		Control.AliveLabels == 0 &&
		Control.AliveFormattedStrings == 0 &&
		Control.AlivePayloads == 0 &&
		Current.RetainedRendererPeers == Cycles &&
		Current.AliveRenderers == Cycles &&
		Current.RenderersWithFormattedString == Cycles &&
		Current.AliveLabels == 0 &&
		Current.AliveFormattedStrings == Cycles &&
		Current.AlivePayloads == Cycles;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var currentMiB = Current.RetainedPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosLabelRendererFormattedStringRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload size per formatted string: {PayloadMegabytesPerCycle} MiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained formatted payload: {controlMiB:N1} MiB",
			$"Current retained formatted payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var payloadMiB = result.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained renderer peers: {result.RetainedRendererPeers}/{result.TrackedCycles}",
			$"  renderers with _formatted assigned: {result.RenderersWithFormattedString}/{result.TrackedCycles}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive Labels: {result.AliveLabels}/{result.TrackedCycles}",
			$"  alive PayloadFormattedStrings: {result.AliveFormattedStrings}/{result.TrackedCycles}",
			$"  alive payloads: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  retained managed payload MiB: {payloadMiB:N1}");
	}
}

sealed class PayloadFormattedString : FormattedString
{
	public PayloadFormattedString(DisclosurePayload payload)
	{
		Payload = payload;
	}

	public DisclosurePayload Payload { get; }
}

internal sealed class DisclosurePayload
{
	public DisclosurePayload(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		SessionBytes = new byte[payloadBytes];

		for (var i = 0; i < SessionBytes.Length; i += 4096)
			SessionBytes[i] = (byte)(cycle + i);

		Rows = Enumerable.Range(1, 8)
			.Select(index => new DisclosureRow(
				$"DISC-{cycle + 1:000}-{index:000}",
				$"Customer disclosure row {index}",
				$"Rich text payload slice {cycle + 1}.{index}"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] SessionBytes { get; }

	public IReadOnlyList<DisclosureRow> Rows { get; }
}

internal sealed record DisclosureRow(string Id, string Title, string State);

sealed class ContextOnlyElementHandler : IPlatformViewHandler
{
	public ContextOnlyElementHandler(IMauiContext mauiContext)
	{
		MauiContext = mauiContext;
		PlatformView = new UIView(CGRect.Empty);
	}

	public bool HasContainer { get; set; }

	UIView? IPlatformViewHandler.ContainerView => null;

	object? IViewHandler.ContainerView => null;

	public UIView? PlatformView { get; private set; }

	object? IElementHandler.PlatformView => PlatformView;

	public IView? VirtualView { get; private set; }

	IElement? IElementHandler.VirtualView => VirtualView;

	public UIViewController? ViewController => null;

	public IMauiContext? MauiContext { get; private set; }

	public void SetMauiContext(IMauiContext mauiContext) => MauiContext = mauiContext;

	public void SetVirtualView(IElement view) => VirtualView = (IView)view;

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
		MauiContext = null;
		PlatformView?.Dispose();
		PlatformView = null;
	}

	public Size GetDesiredSize(double widthConstraint, double heightConstraint) => Size.Zero;

	public void PlatformArrange(Rect frame)
	{
	}
}
