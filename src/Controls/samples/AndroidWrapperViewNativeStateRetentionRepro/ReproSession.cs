#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace AndroidWrapperViewNativeStateRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 96;
	const int PayloadBytesPerNativeSlot = 512 * 1024;

	static readonly List<WrapperView> RetainedNativeWrappers = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear WrapperView Clip and Shadow after handler disconnect",
			context,
			clearWrapperState: true);

		var current = await RunScenarioAsync(
			"current: ViewHandler disconnect leaves WrapperView Clip and Shadow assigned",
			context,
			clearWrapperState: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeWrappers);

		return new ReproReport(
			Cycles,
			PayloadBytesPerNativeSlot,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearWrapperState)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateDisconnectedWrapperCycle(context, i, tracked, clearWrapperState);

			if (i % 16 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateDisconnectedWrapperCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearWrapperState)
	{
		var geometry = new PayloadGeometry(cycle, PayloadBytesPerNativeSlot);
		var shadow = new PayloadShadow(cycle, PayloadBytesPerNativeSlot)
		{
			Brush = Brush.Black,
			Offset = new Point(6, 6),
			Opacity = 0.72f,
			Radius = 24
		};

		var view = new BoxView
		{
			Color = Colors.Teal,
			Clip = geometry,
			Shadow = shadow,
			InputTransparent = true,
			WidthRequest = 120,
			HeightRequest = 64
		};

		var handler = new BoxViewHandler();
		AttachHandler(view, handler, context);

		var wrapper = (WrapperView?)handler.ContainerView
			?? throw new InvalidOperationException("BoxViewHandler did not create an Android WrapperView.");

		((IElementHandler)handler).DisconnectHandler();
		view.Handler = null;

		if (clearWrapperState)
		{
			wrapper.Clip = null;
			wrapper.Shadow = null;
			wrapper.InputTransparent = false;
		}

		RetainedNativeWrappers.Add(wrapper);
		tracked.Add(TrackedCycle.Create(cycle, wrapper, view, handler, geometry, shadow));
	}

	static void AttachHandler(IElement view, IElementHandler handler, IMauiContext context)
	{
		handler.SetMauiContext(context);
		view.Handler = handler;
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

	internal sealed class PayloadGeometry : Geometry
	{
		public PayloadGeometry(int cycle, int payloadBytes)
		{
			Cycle = cycle;
			PayloadBytes = payloadBytes;
			Payload = CreatePayload(cycle, payloadBytes, 0x47);
			Segments = CreateSegments("clip", cycle);
		}

		public int Cycle { get; }

		public int PayloadBytes { get; }

		public byte[] Payload { get; }

		public IReadOnlyList<VisualRule> Segments { get; }

		public override void AppendPath(PathF path)
		{
			path.AppendRectangle(0, 0, 120, 64);
		}
	}

	internal sealed class PayloadShadow : Shadow
	{
		public PayloadShadow(int cycle, int payloadBytes)
		{
			Cycle = cycle;
			PayloadBytes = payloadBytes;
			Payload = CreatePayload(cycle, payloadBytes, 0x53);
			Tokens = CreateSegments("shadow", cycle);
		}

		public int Cycle { get; }

		public int PayloadBytes { get; }

		public byte[] Payload { get; }

		public IReadOnlyList<VisualRule> Tokens { get; }
	}

	internal sealed record VisualRule(string Id, string Scope, string Value);

	static VisualRule[] CreateSegments(string scope, int cycle)
	{
		var rules = new VisualRule[12];
		for (var i = 0; i < rules.Length; i++)
			rules[i] = new VisualRule($"{scope}-{cycle:D4}-{i:D2}", scope, $"resolved-style-token-{cycle:D4}-{i:D2}");

		return rules;
	}

	static byte[] CreatePayload(int cycle, int payloadBytes, byte seed)
	{
		var payload = new byte[payloadBytes];
		for (var i = 0; i < payload.Length; i += 4096)
			payload[i] = (byte)(seed + cycle + i);

		return payload;
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<WrapperView> NativeWrapper,
		WeakReference<BoxView> VirtualView,
		WeakReference<IElementHandler> Handler,
		WeakReference<PayloadGeometry> Geometry,
		WeakReference<byte[]> GeometryPayload,
		WeakReference<PayloadShadow> Shadow,
		WeakReference<byte[]> ShadowPayload,
		long PayloadBytesPerSlot)
	{
		public static TrackedCycle Create(
			int cycle,
			WrapperView wrapper,
			BoxView view,
			IElementHandler handler,
			PayloadGeometry geometry,
			PayloadShadow shadow)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<WrapperView>(wrapper),
				new WeakReference<BoxView>(view),
				new WeakReference<IElementHandler>(handler),
				new WeakReference<PayloadGeometry>(geometry),
				new WeakReference<byte[]>(geometry.Payload),
				new WeakReference<PayloadShadow>(shadow),
				new WeakReference<byte[]>(shadow.Payload),
				geometry.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeWrappers,
		int AliveVirtualViews,
		int AliveHandlers,
		int AliveGeometries,
		int AliveGeometryPayloads,
		int AliveShadows,
		int AliveShadowPayloads,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeWrappers = 0;
			var aliveVirtualViews = 0;
			var aliveHandlers = 0;
			var aliveGeometries = 0;
			var aliveGeometryPayloads = 0;
			var aliveShadows = 0;
			var aliveShadowPayloads = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeWrapper.TryGetTarget(out _))
					aliveNativeWrappers++;

				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;

				if (cycle.Geometry.TryGetTarget(out _))
					aliveGeometries++;

				if (cycle.GeometryPayload.TryGetTarget(out _))
				{
					aliveGeometryPayloads++;
					retainedPayloadBytes += cycle.PayloadBytesPerSlot;
				}

				if (cycle.Shadow.TryGetTarget(out _))
					aliveShadows++;

				if (cycle.ShadowPayload.TryGetTarget(out _))
				{
					aliveShadowPayloads++;
					retainedPayloadBytes += cycle.PayloadBytesPerSlot;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeWrappers,
				aliveVirtualViews,
				aliveHandlers,
				aliveGeometries,
				aliveGeometryPayloads,
				aliveShadows,
				aliveShadowPayloads,
				retainedPayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadBytesPerNativeSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveNativeWrappers == Cycles &&
		Current.AliveNativeWrappers == Cycles &&
		Control.AliveGeometryPayloads == 0 &&
		Control.AliveShadowPayloads == 0 &&
		Current.AliveGeometryPayloads == Cycles &&
		Current.AliveShadowPayloads == Cycles &&
		Current.AliveVirtualViews == 0 &&
		Current.AliveHandlers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidWrapperViewNativeStateRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload bytes per wrapper native state slot: {PayloadBytesPerNativeSlot:N0}",
			"Source paths mirrored: ViewHandler.MapContainerView, MapClip, MapShadow, MapInputTransparent, Android WrapperView Clip/Shadow fields, and ElementHandler disconnect",
			"Retained peers: native Android WrapperView containers only",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained wrapper payload: {controlMiB:N1} MiB",
			$"Current retained wrapper payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native WrapperViews: {result.AliveNativeWrappers}/{result.TrackedCycles}",
			$"  alive BoxViews: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive clip geometries: {result.AliveGeometries}/{result.TrackedCycles}",
			$"  alive clip payload byte arrays: {result.AliveGeometryPayloads}/{result.TrackedCycles}",
			$"  alive shadows: {result.AliveShadows}/{result.TrackedCycles}",
			$"  alive shadow payload byte arrays: {result.AliveShadowPayloads}/{result.TrackedCycles}",
			$"  retained payload bytes: {result.RetainedPayloadBytes:N0}");
	}
}
