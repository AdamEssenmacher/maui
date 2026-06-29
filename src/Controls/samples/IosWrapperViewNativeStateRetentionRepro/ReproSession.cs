#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using ObjCRuntime;
using UIKit;

namespace IosWrapperViewNativeStateRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerNativeSlot = 512;
	const long PayloadBytesPerNativeSlot = PayloadKiBPerNativeSlot * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedWrapperPeer>> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		System.IO.Path.Combine("/tmp", "ios-wrapperview-native-state-retention-results.txt");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		WriteProgress("Starting iOS WrapperView native state retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: clear WrapperView Clip after handler disconnect",
			mauiContext,
			clearWrapperStateAfterDisconnect: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: handler disconnect leaves WrapperView Clip assigned",
			mauiContext,
			clearWrapperStateAfterDisconnect: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadKiBPerNativeSlot,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(
		string name,
		IMauiContext mauiContext,
		bool clearWrapperStateAfterDisconnect)
	{
		var retainedPeers = new List<RetainedWrapperPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 12 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateWrapperCycle(i, mauiContext, retainedPeers, tracked, clearWrapperStateAfterDisconnect);
		}

		RetainedNativePeers.Add(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, retainedPeers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateWrapperCycle(
		int cycle,
		IMauiContext mauiContext,
		List<RetainedWrapperPeer> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearWrapperStateAfterDisconnect)
	{
		using var pool = new NSAutoreleasePool();

		var geometry = new PayloadGeometry(cycle);
		var view = new BoxView
		{
			Color = Colors.Teal,
			Clip = geometry,
			WidthRequest = 180,
			HeightRequest = 96
		};

		var handler = (BoxViewHandler)view.ToHandler(mauiContext);
		handler.UpdateValue(nameof(IViewHandler.ContainerView));
		handler.UpdateValue(nameof(IView.Clip));

		var wrapper = handler.ContainerView
			?? throw new InvalidOperationException("BoxViewHandler did not create an iOS WrapperView.");

		SetRealisticBounds(wrapper, 180, 96);

		if (wrapper.Clip is not PayloadGeometry)
			throw new InvalidOperationException(
				"WrapperView did not receive the expected Clip payload. " +
				$"Wrapper Clip={wrapper.Clip?.GetType().FullName ?? "<null>"}, " +
				$"IView Clip={((IView)view).Clip?.GetType().FullName ?? "<null>"}.");

		var retainedPeer = RetainNativePeer(wrapper);

		((IElementHandler)handler).DisconnectHandler();
		view.ClearValue(VisualElement.ClipProperty);
		view.BindingContext = null;
		view.Handler = null!;

		if (clearWrapperStateAfterDisconnect)
		{
			wrapper.Clip = null;
			wrapper.Border = null;
		}

		retainedPeers.Add(retainedPeer);
		tracked.Add(TrackedCycle.Create(cycle, handler, view, geometry));
	}

	static void SetRealisticBounds(UIView view, int width, int height)
	{
		var bounds = new CGRect(0, 0, width, height);
		view.Frame = bounds;
		view.Bounds = bounds;
	}

	static RetainedWrapperPeer RetainNativePeer(WrapperView wrapper)
	{
		var handle = wrapper.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native WrapperView peer with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedWrapperPeer(retained);
	}

	static WrapperSnapshot GetWrapperSnapshot(RetainedWrapperPeer retainedPeer)
	{
		var peer = retainedPeer.TryGetPeer();
		if (peer is null)
			return new WrapperSnapshot(Alive: false, HasClip: false, RetainedPayloadBytes: 0);

		long retainedPayloadBytes = 0;
		var hasClip = false;

		if (peer.Clip is PayloadGeometry geometry)
		{
			hasClip = true;
			retainedPayloadBytes += geometry.Payload.LongLength;
		}

		return new WrapperSnapshot(
			Alive: true,
			HasClip: hasClip,
			RetainedPayloadBytes: retainedPayloadBytes);
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

	static byte[] CreatePayload(int cycle, byte seed)
	{
		var payload = new byte[PayloadBytesPerNativeSlot];
		for (var i = 0; i < payload.Length; i += 4096)
			payload[i] = (byte)(seed + cycle + i);

		return payload;
	}

	[DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

	internal sealed class PayloadGeometry : Geometry
	{
		public PayloadGeometry(int cycle)
		{
			Cycle = cycle;
			Payload = CreatePayload(cycle, 0x47);
			Rules = CreateRules("clip", cycle);
		}

		public int Cycle { get; }

		public byte[] Payload { get; }

		public IReadOnlyList<VisualRule> Rules { get; }

		public override void AppendPath(PathF path)
		{
			path.AppendRoundedRectangle(0, 0, 180, 96, 18);
		}
	}

	internal sealed record VisualRule(string Id, string Scope, string Value);

	static VisualRule[] CreateRules(string scope, int cycle)
	{
		var rules = new VisualRule[12];
		for (var i = 0; i < rules.Length; i++)
			rules[i] = new VisualRule($"{scope}-{cycle:D4}-{i:D2}", scope, $"resolved-style-token-{cycle:D4}-{i:D2}");

		return rules;
	}

	internal sealed record WrapperSnapshot(bool Alive, bool HasClip, long RetainedPayloadBytes);

	internal sealed class RetainedWrapperPeer
	{
		public RetainedWrapperPeer(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public WrapperView? TryGetPeer()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<WrapperView>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<object> Handler,
		WeakReference<object> VirtualView,
		WeakReference<PayloadGeometry> Geometry,
		WeakReference<byte[]> GeometryPayload)
	{
		public static TrackedCycle Create(
			int cycle,
			object handler,
			object virtualView,
			PayloadGeometry geometry)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<object>(handler),
				new WeakReference<object>(virtualView),
				new WeakReference<PayloadGeometry>(geometry),
				new WeakReference<byte[]>(geometry.Payload));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativePeers,
		int NativePeersWithClip,
		int AliveHandlers,
		int AliveVirtualViews,
		int AliveGeometries,
		int AliveGeometryPayloads,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedWrapperPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativePeers = 0;
			var nativePeersWithClip = 0;
			long retainedPayloadBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				var snapshot = GetWrapperSnapshot(retainedPeer);
				if (!snapshot.Alive)
					continue;

				retainedNativePeers++;
				if (snapshot.HasClip)
					nativePeersWithClip++;
				retainedPayloadBytes += snapshot.RetainedPayloadBytes;
			}

			var aliveHandlers = 0;
			var aliveVirtualViews = 0;
			var aliveGeometries = 0;
			var aliveGeometryPayloads = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;
				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;
				if (cycle.Geometry.TryGetTarget(out _))
					aliveGeometries++;
				if (cycle.GeometryPayload.TryGetTarget(out _))
					aliveGeometryPayloads++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativePeers,
				nativePeersWithClip,
				aliveHandlers,
				aliveVirtualViews,
				aliveGeometries,
				aliveGeometryPayloads,
				retainedPayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerNativeSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativePeers == Cycles &&
		Control.NativePeersWithClip == 0 &&
		Control.AliveHandlers <= 1 &&
		Control.AliveVirtualViews <= 1 &&
		Control.AliveGeometryPayloads == 0 &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativePeersWithClip == Cycles &&
		Current.AliveHandlers <= 1 &&
		Current.AliveVirtualViews <= 1 &&
		Current.AliveGeometryPayloads == Cycles &&
		Current.RetainedPayloadBytes >= Cycles * PayloadKiBPerNativeSlot * 1024L;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosWrapperViewNativeStateRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per wrapper native state slot: {PayloadKiBPerNativeSlot} KiB",
			"Source paths mirrored: ViewHandler.MapContainerView, MapClip, iOS WrapperView Clip field, and handler disconnect",
			"Retained peers: native iOS/Mac Catalyst WrapperView peers only",
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
		var retainedMiB = result.RetainedPayloadBytes / 1024d / 1024d;
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native WrapperView peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native peers with assigned Clip: {result.NativePeersWithClip}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive BoxViews: {result.AliveVirtualViews}/{result.TrackedCycles}",
			$"  alive clip geometries: {result.AliveGeometries}/{result.TrackedCycles}",
			$"  alive clip payload byte arrays: {result.AliveGeometryPayloads}/{result.TrackedCycles}",
			$"  retained payload bytes: {result.RetainedPayloadBytes:N0}",
			$"  retained payload MiB: {retainedMiB:N1}");
	}
}
