#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;

namespace AndroidGraphicsViewDrawableRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadBytesPerDrawable = 1024 * 1024;

	static readonly List<PlatformTouchGraphicsView> RetainedNativePeers = new();

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

		GC.KeepAlive(RetainedNativePeers);

		return Task.FromResult(new ReproReport(
			Cycles,
			PayloadBytesPerDrawable,
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
		var drawable = new PayloadDrawable(cycle, PayloadBytesPerDrawable);
		var graphicsView = new GraphicsView
		{
			Drawable = drawable,
			WidthRequest = 320,
			HeightRequest = 180
		};

		var platformView = new PlatformTouchGraphicsView(Android.App.Application.Context);

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
		public PayloadDrawable(int cycle, int payloadBytes)
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

		public int PayloadBytes { get; }

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
		WeakReference<PlatformTouchGraphicsView> NativePeer,
		WeakReference<GraphicsView> GraphicsView,
		WeakReference<PayloadDrawable> Drawable,
		WeakReference<byte[]> Payload,
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
				new WeakReference<PlatformTouchGraphicsView>(platformView),
				new WeakReference<GraphicsView>(graphicsView),
				new WeakReference<PayloadDrawable>(drawable),
				new WeakReference<byte[]>(drawable.TileBytes),
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
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativePeers = 0;
			var aliveGraphicsViews = 0;
			var aliveDrawables = 0;
			var alivePayloads = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativePeer.TryGetTarget(out _))
					aliveNativePeers++;

				if (cycle.GraphicsView.TryGetTarget(out _))
					aliveGraphicsViews++;

				if (cycle.Drawable.TryGetTarget(out _))
					aliveDrawables++;

				if (cycle.Payload.TryGetTarget(out _))
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
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadBytesPerDrawable,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AlivePayloads == 0 &&
		Current.AliveNativePeers == Cycles &&
		Current.AliveDrawables == Cycles &&
		Current.AlivePayloads == Cycles;

	public string ToText()
	{
		var retainedMiB = Current.RetainedPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidGraphicsViewDrawableRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per drawable: {PayloadBytesPerDrawable / 1024 / 1024} MiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained payload: {controlMiB:N1} MiB",
			$"Current retained payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native PlatformTouchGraphicsViews: {result.AliveNativePeers}/{result.TrackedCycles}",
			$"  alive GraphicsViews: {result.AliveGraphicsViews}/{result.TrackedCycles}",
			$"  alive IDrawables: {result.AliveDrawables}/{result.TrackedCycles}",
			$"  alive payload byte arrays: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  retained payload bytes: {result.RetainedPayloadBytes:N0}");
	}
}
