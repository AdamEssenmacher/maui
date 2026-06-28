#if IOS || MACCATALYST
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;

namespace GraphicsViewDrawableRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerDrawable = 1;

	static readonly List<PlatformTouchGraphicsView> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		"/tmp/graphicsview-drawable-retention-results.txt";

	public static Task<ReproReport> RunAsync()
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunScenario(
			"control: clear native PlatformGraphicsView.Drawable during disconnect",
			clearNativeDrawable: true);

		var current = RunScenario(
			"current: PlatformTouchGraphicsView.Disconnect leaves Drawable assigned",
			clearNativeDrawable: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return Task.FromResult(new ReproReport(
			Cycles,
			PayloadMegabytesPerDrawable,
			baselineBytes,
			finalBytes,
			control,
			current));
	}

	static ScenarioResult RunScenario(string name, bool clearNativeDrawable)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
			CreateDisconnectedNativePeerCycle(i, tracked, clearNativeDrawable);

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateDisconnectedNativePeerCycle(
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeDrawable)
	{
		using var pool = new NSAutoreleasePool();

		var drawable = new PayloadDrawable(
			cycle,
			PayloadMegabytesPerDrawable * 1024L * 1024L);

		var graphicsView = new GraphicsView
		{
			Drawable = drawable,
			WidthRequest = 320,
			HeightRequest = 180
		};

		var platformView = new PlatformTouchGraphicsView
		{
			Frame = new CoreGraphics.CGRect(0, 0, 320, 180)
		};

		platformView.Connect(graphicsView);
		platformView.UpdateDrawable(graphicsView);
		platformView.Disconnect();

		if (clearNativeDrawable)
			platformView.Drawable = null;

		RetainedNativePeers.Add(platformView);
		tracked.Add(TrackedCycle.Create(cycle, platformView, graphicsView, drawable));
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

	internal sealed class PayloadDrawable : IDrawable
	{
		public PayloadDrawable(int cycle, long payloadBytes)
		{
			Cycle = cycle;
			PayloadBytes = payloadBytes;
			TileBytes = new byte[payloadBytes];

			for (var i = 0; i < TileBytes.Length; i += 4096)
				TileBytes[i] = (byte)(cycle + i);

			Segments = Enumerable.Range(1, 12)
				.Select(index => new DashboardSegment(
					$"asset-{cycle + 1:000}-{index:000}",
					$"Metric {index}",
					$"Rendered dashboard series {cycle + 1}.{index}"))
				.ToArray();
		}

		public int Cycle { get; }

		public long PayloadBytes { get; }

		public byte[] TileBytes { get; }

		public IReadOnlyList<DashboardSegment> Segments { get; }

		public void Draw(ICanvas canvas, RectF dirtyRect)
		{
			canvas.FillColor = Colors.MidnightBlue;
			canvas.FillRectangle(dirtyRect);
			canvas.StrokeColor = Colors.DeepSkyBlue;
			canvas.StrokeSize = 2;
			canvas.DrawRectangle(dirtyRect);
		}
	}

	internal sealed record DashboardSegment(string Id, string Label, string State);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference NativePeer,
		WeakReference GraphicsView,
		WeakReference Drawable,
		WeakReference Payload,
		long PayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			PlatformTouchGraphicsView platformView,
			GraphicsView graphicsView,
			PayloadDrawable drawable)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference(platformView),
				new WeakReference(graphicsView),
				new WeakReference(drawable),
				new WeakReference(drawable.TileBytes),
				drawable.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativePeers,
		int AliveGraphicsViews,
		int AliveDrawables,
		int AlivePayloads,
		long RetainedPayloadBytes)
	{
		public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativePeers = 0;
			var aliveGraphicsViews = 0;
			var aliveDrawables = 0;
			var alivePayloads = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.IsAlive)
					aliveNativePeers++;

				if (cycle.GraphicsView.IsAlive)
					aliveGraphicsViews++;

				if (cycle.Drawable.IsAlive)
					aliveDrawables++;

				if (cycle.Payload.IsAlive)
				{
					alivePayloads++;
					retainedPayloadBytes += cycle.PayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativePeers,
				aliveGraphicsViews,
				aliveDrawables,
				alivePayloads,
				retainedPayloadBytes);
		}
	}

	internal sealed record ReproReport(
		int Cycles,
		int PayloadMegabytesPerDrawable,
		long BaselineManagedBytes,
		long FinalManagedBytes,
		ScenarioResult Control,
		ScenarioResult Current)
	{
		public string ToText()
		{
			var retainedMiB = Current.RetainedPayloadBytes / 1024d / 1024d;
			var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
			var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
			var proven = Control.AlivePayloads == 0 &&
				Current.AliveNativePeers == Cycles &&
				Current.AliveDrawables == Cycles &&
				Current.AlivePayloads == Cycles;

			return string.Join(Environment.NewLine, new[]
			{
				"GraphicsView native drawable retention repro",
				$"Cycles: {Cycles}",
				$"Payload per drawable: {PayloadMegabytesPerDrawable} MiB",
				$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
				$"Final managed heap: {FinalManagedBytes:N0} bytes",
				$"Managed heap delta: {heapDeltaMiB:N1} MiB",
				string.Empty,
				FormatScenario(Control),
				string.Empty,
				FormatScenario(Current),
				string.Empty,
				$"Control retained payload: {controlMiB:N1} MiB",
				$"Current retained payload: {retainedMiB:N1} MiB",
				$"RESULT: {(proven ? "PROVEN" : "NOT PROVEN")}"
			});
		}

		static string FormatScenario(ScenarioResult result)
		{
			return string.Join(Environment.NewLine, new[]
			{
				result.Name,
				$"  tracked cycles: {result.TrackedCycles}",
				$"  alive native PlatformTouchGraphicsViews: {result.AliveNativePeers}/{result.TrackedCycles}",
				$"  alive GraphicsViews: {result.AliveGraphicsViews}/{result.TrackedCycles}",
				$"  alive IDrawables: {result.AliveDrawables}/{result.TrackedCycles}",
				$"  alive payload byte arrays: {result.AlivePayloads}/{result.TrackedCycles}",
				$"  retained payload bytes: {result.RetainedPayloadBytes:N0}"
			});
		}
	}
}
#else
namespace GraphicsViewDrawableRetentionRepro;

internal static class ReproSession
{
	public static readonly string ResultsPath =
		"/tmp/graphicsview-drawable-retention-results.txt";

	public static Task<ReproReport> RunAsync() =>
		Task.FromResult(new ReproReport("This repro targets iOS and Mac Catalyst."));

	internal sealed record ReproReport(string Message)
	{
		public string ToText() => Message;
	}
}
#endif
