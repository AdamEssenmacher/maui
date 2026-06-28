#nullable enable

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using Microsoft.Maui.Graphics;
using UIKit;

namespace IosBoxRendererBackgroundPatternRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 16;
	internal const int BoxWidthPoints = 512;
	internal const int BoxHeightPoints = 512;
	internal const int PayloadMegabytesPerCycle = 1;

	static readonly FieldInfo ColorToRendererField =
		typeof(BoxRenderer).GetField("_colorToRenderer", BindingFlags.NonPublic | BindingFlags.Instance)
		?? throw new InvalidOperationException("Could not find BoxRenderer._colorToRenderer.");

	static readonly List<IReadOnlyList<BoxRenderer>> RetainedRendererPeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-boxrenderer-background-pattern-retention-results.txt");

	public static ReproReport Run()
	{
		WriteProgress("Starting iOS BoxRenderer background pattern retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: dispose renderer and clear private pattern color",
			clearPatternColorAfterDispose: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: BoxRenderer dispose leaves private pattern color assigned",
			clearPatternColorAfterDispose: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedRendererPeers);

		return new ReproReport(
			Cycles,
			BoxWidthPoints,
			BoxHeightPoints,
			GetDisplayScale(),
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(
		string name,
		bool clearPatternColorAfterDispose)
	{
		var tracking = RunScenarioCore(name, clearPatternColorAfterDispose);
		RetainedRendererPeers.Add(tracking.Renderers);
		ForceFullGc();

		return ScenarioResult.From(name, tracking.Renderers, tracking.TrackedCycles);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioTracking RunScenarioCore(
		string name,
		bool clearPatternColorAfterDispose)
	{
		var renderers = new List<BoxRenderer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 4 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateDisposedRendererCycle(i, renderers, tracked, clearPatternColorAfterDispose);
		}

		return new ScenarioTracking(renderers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisposedRendererCycle(
		int cycle,
		List<BoxRenderer> renderers,
		List<TrackedCycle> tracked,
		bool clearPatternColorAfterDispose)
	{
		var payload = new BoxPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var brush = CreateBackground(cycle);
		var boxView = new PayloadBoxView(cycle, payload, brush);
		var renderer = new BoxRenderer();

		SetRealisticBounds(renderer);
		renderer.SetElement(boxView);
		renderer.LayoutSubviews();

		if (!HasPatternColor(renderer))
			throw new InvalidOperationException("BoxRenderer did not assign a pattern-image color.");

		renderer.Dispose();
		((IElementController)boxView).EffectControlProvider = null;
		boxView.Handler = null;

		if (clearPatternColorAfterDispose)
			ClearPatternColor(renderer);

		renderers.Add(renderer);
		tracked.Add(TrackedCycle.Create(cycle, renderer, boxView, payload, brush));
	}

	static void SetRealisticBounds(BoxRenderer renderer)
	{
		var bounds = new CGRect(0, 0, BoxWidthPoints, BoxHeightPoints);
		renderer.Frame = bounds;
		renderer.Bounds = bounds;
	}

	static Brush CreateBackground(int cycle)
	{
		return new LinearGradientBrush
		{
			StartPoint = new Point(0, 0),
			EndPoint = new Point(1, 1),
			GradientStops =
			{
				new GradientStop(Color.FromRgb((cycle * 31) % 255, 72, 120), 0),
				new GradientStop(Color.FromRgb(32, (cycle * 53) % 255, 190), 0.55f),
				new GradientStop(Color.FromRgb(16, 24, (cycle * 79) % 255), 1)
			}
		};
	}

	static UIColor? GetPatternColor(BoxRenderer renderer) =>
		(UIColor?)ColorToRendererField.GetValue(renderer);

	static void ClearPatternColor(BoxRenderer renderer)
	{
		var color = GetPatternColor(renderer);
		ColorToRendererField.SetValue(renderer, null);
		color?.Dispose();
	}

	static bool HasPatternColor(BoxRenderer renderer)
	{
		var color = GetPatternColor(renderer);
		if (color is null)
			return false;

		nfloat red;
		nfloat green;
		nfloat blue;
		nfloat alpha;

		try
		{
			color.GetRGBA(out red, out green, out blue, out alpha);
		}
		catch
		{
			return true;
		}

		const double tolerance = 0.001;
		return Math.Abs(red) > tolerance ||
			Math.Abs(green) > tolerance ||
			Math.Abs(blue) > tolerance ||
			Math.Abs(alpha) > tolerance;
	}

	static nfloat GetDisplayScale() => UIScreen.MainScreen.Scale <= 0 ? 1 : UIScreen.MainScreen.Scale;

	static long EstimatePatternImageBytes()
	{
		var scale = GetDisplayScale();
		var width = Math.Max(1, (int)Math.Ceiling(BoxWidthPoints * scale));
		var height = Math.Max(1, (int)Math.Ceiling(BoxHeightPoints * scale));
		return width * (long)height * 4;
	}

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
		IReadOnlyList<BoxRenderer> Renderers,
		IReadOnlyList<TrackedCycle> TrackedCycles);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<BoxRenderer> Renderer,
		WeakReference<BoxView> BoxView,
		WeakReference<BoxPayload> Payload,
		WeakReference<Brush> Brush,
		long PayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			BoxRenderer renderer,
			BoxView boxView,
			BoxPayload payload,
			Brush brush)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<BoxRenderer>(renderer),
				new WeakReference<BoxView>(boxView),
				new WeakReference<BoxPayload>(payload),
				new WeakReference<Brush>(brush),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedRendererPeers,
		int RenderersWithPatternColor,
		long EstimatedPatternImageBytes,
		int AliveRenderers,
		int AliveBoxViews,
		int AlivePayloads,
		int AliveBrushes,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<BoxRenderer> renderers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var renderersWithPatternColor = 0;
			foreach (var renderer in renderers)
			{
				if (HasPatternColor(renderer))
					renderersWithPatternColor++;
			}

			var aliveRenderers = 0;
			var aliveBoxViews = 0;
			var alivePayloads = 0;
			var aliveBrushes = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.BoxView.TryGetTarget(out _))
					aliveBoxViews++;

				if (cycle.Payload.TryGetTarget(out _))
				{
					alivePayloads++;
					retainedPayloadBytes += cycle.PayloadBytes;
				}

				if (cycle.Brush.TryGetTarget(out _))
					aliveBrushes++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				renderers.Count,
				renderersWithPatternColor,
				renderersWithPatternColor * EstimatePatternImageBytes(),
				aliveRenderers,
				aliveBoxViews,
				alivePayloads,
				aliveBrushes,
				retainedPayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int BoxWidthPoints,
	int BoxHeightPoints,
	nfloat DisplayScale,
	int PayloadMegabytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedRendererPeers == Cycles &&
		Control.AliveRenderers == Cycles &&
		Control.RenderersWithPatternColor == 0 &&
		Control.AliveBoxViews == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AliveBrushes == 0 &&
		Current.RetainedRendererPeers == Cycles &&
		Current.AliveRenderers == Cycles &&
		Current.RenderersWithPatternColor == Cycles &&
		Current.EstimatedPatternImageBytes > 0 &&
		Current.AliveBoxViews == 0 &&
		Current.AlivePayloads == 0 &&
		Current.AliveBrushes == 0;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedPatternImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedPatternImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosBoxRendererBackgroundPatternRetentionRepro",
			$"Cycles: {Cycles}",
			$"Box size: {BoxWidthPoints} x {BoxHeightPoints} points",
			$"Display scale: {DisplayScale:N1}",
			$"Payload size per BoxView: {PayloadMegabytesPerCycle} MiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native pattern image payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native pattern image payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeImageMiB = result.EstimatedPatternImageBytes / 1024d / 1024d;
		var payloadMiB = result.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained renderer peers: {result.RetainedRendererPeers}/{result.TrackedCycles}",
			$"  renderers with pattern color: {result.RenderersWithPatternColor}/{result.TrackedCycles}",
			$"  estimated assigned native image bytes: {result.EstimatedPatternImageBytes:N0}",
			$"  estimated assigned native image MiB: {nativeImageMiB:N1}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive BoxViews: {result.AliveBoxViews}/{result.TrackedCycles}",
			$"  alive payloads: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  alive brushes: {result.AliveBrushes}/{result.TrackedCycles}",
			$"  retained managed payload MiB: {payloadMiB:N1}");
	}
}

sealed class PayloadBoxView : BoxView
{
	public PayloadBoxView(int cycle, BoxPayload payload, Brush background)
	{
		AutomationId = $"boxrenderer-background-pattern-{cycle + 1}";
		BindingContext = payload;
		Background = background;
		WidthRequest = ReproSession.BoxWidthPoints;
		HeightRequest = ReproSession.BoxHeightPoints;
		CornerRadius = new CornerRadius(18, 28, 18, 28);
	}
}

internal sealed class BoxPayload
{
	public BoxPayload(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		SessionBytes = new byte[payloadBytes];

		for (var i = 0; i < SessionBytes.Length; i += 4096)
			SessionBytes[i] = (byte)(cycle + i);

		Tiles = Enumerable.Range(1, 12)
			.Select(index => new TileState(
				$"BOX-{cycle + 1:000}-{index:000}",
				$"Operational tile {index}",
				$"Gradient visual state {cycle + 1}.{index}"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] SessionBytes { get; }

	public IReadOnlyList<TileState> Tiles { get; }
}

internal sealed record TileState(string Id, string Title, string State);
