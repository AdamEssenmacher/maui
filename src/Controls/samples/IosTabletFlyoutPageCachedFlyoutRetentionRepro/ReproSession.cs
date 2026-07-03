using System.Reflection;
using System.Runtime.CompilerServices;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;

namespace IosTabletFlyoutPageCachedFlyoutRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 80;
	internal const int PayloadMegabytesPerCycle = 1;

	static readonly List<IReadOnlyList<TabletFlyoutPageRenderer>> RetainedRendererPeerSets = new();
	static readonly FieldInfo EventsField =
		typeof(TabletFlyoutPageRenderer).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)
		?? throw new InvalidOperationException("Could not find TabletFlyoutPageRenderer._events.");
	static readonly FieldInfo CachedFlyoutPageField =
		typeof(TabletFlyoutPageRenderer).GetField("_flyoutPage", BindingFlags.NonPublic | BindingFlags.Instance)
		?? throw new InvalidOperationException("Could not find TabletFlyoutPageRenderer._flyoutPage.");

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-tabletflyoutpage-cached-flyout-retention-results.txt");

	public static ReproReport Run()
	{
		WriteProgress("Starting iOS TabletFlyoutPage cached FlyoutPage retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: dispose renderer and clear cached _flyoutPage",
			clearCachedFlyoutPageAfterDispose: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: dispose renderer with cached _flyoutPage still assigned",
			clearCachedFlyoutPageAfterDispose: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedRendererPeerSets);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(string name, bool clearCachedFlyoutPageAfterDispose)
	{
		var tracking = RunScenarioCore(clearCachedFlyoutPageAfterDispose);
		RetainedRendererPeerSets.Add(tracking.Renderers);

		ForceFullGc();

		return ScenarioResult.From(name, tracking.Renderers, tracking.TrackedCycles);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioTracking RunScenarioCore(bool clearCachedFlyoutPageAfterDispose)
	{
		var renderers = new List<TabletFlyoutPageRenderer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 20 == 0)
				WriteProgress($"cycle {i}/{Cycles}");

			CreateDisposedRendererCycle(i, renderers, tracked, clearCachedFlyoutPageAfterDispose);
		}

		return new ScenarioTracking(renderers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisposedRendererCycle(
		int cycle,
		List<TabletFlyoutPageRenderer> renderers,
		List<TrackedCycle> tracked,
		bool clearCachedFlyoutPageAfterDispose)
	{
		using var pool = new NSAutoreleasePool();
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var flyoutPage = new PayloadFlyoutPage(cycle, payload);
		var renderer = new TabletFlyoutPageRenderer();

		EventsField.SetValue(renderer, new EventTracker(renderer));
		renderer.SetElement(flyoutPage);

		if (!ReferenceEquals(CachedFlyoutPageField.GetValue(renderer), flyoutPage))
			throw new InvalidOperationException("TabletFlyoutPageRenderer did not cache the FlyoutPage in _flyoutPage.");

		renderer.Dispose();
		((IElementController)flyoutPage).EffectControlProvider = null;
		((IElementController)flyoutPage.Flyout).EffectControlProvider = null;
		((IElementController)flyoutPage.Detail).EffectControlProvider = null;
		flyoutPage.Handler = null;
		flyoutPage.Flyout.Handler = null;
		flyoutPage.Detail.Handler = null;

		if (clearCachedFlyoutPageAfterDispose)
			CachedFlyoutPageField.SetValue(renderer, null);

		renderers.Add(renderer);
		tracked.Add(TrackedCycle.Create(cycle, renderer, flyoutPage, payload));
	}

	static int CountRenderersWithCachedFlyoutPage(IReadOnlyList<TabletFlyoutPageRenderer> renderers)
	{
		var count = 0;
		foreach (var renderer in renderers)
		{
			if (CachedFlyoutPageField.GetValue(renderer) is not null)
				count++;
		}

		return count;
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
		IReadOnlyList<TabletFlyoutPageRenderer> Renderers,
		IReadOnlyList<TrackedCycle> TrackedCycles);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference Renderer,
		WeakReference FlyoutPage,
		WeakReference Payload,
		long PayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			TabletFlyoutPageRenderer renderer,
			PayloadFlyoutPage flyoutPage,
			LeakPayload payload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference(renderer),
				new WeakReference(flyoutPage),
				new WeakReference(payload),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int RetainedRendererPeers,
		int TrackedCycles,
		int RenderersWithCachedFlyoutPage,
		int AliveRenderers,
		int AliveFlyoutPages,
		int AlivePayloads,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<TabletFlyoutPageRenderer> renderers,
			IReadOnlyList<TrackedCycle> cycles)
		{
			var aliveRenderers = 0;
			var aliveFlyoutPages = 0;
			var alivePayloads = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in cycles)
			{
				if (cycle.Renderer.IsAlive)
					aliveRenderers++;
				if (cycle.FlyoutPage.IsAlive)
					aliveFlyoutPages++;
				if (cycle.Payload.IsAlive)
				{
					alivePayloads++;
					retainedPayloadBytes += cycle.PayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				renderers.Count,
				cycles.Count,
				CountRenderersWithCachedFlyoutPage(renderers),
				aliveRenderers,
				aliveFlyoutPages,
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
	public bool Proven =>
		Control.RetainedRendererPeers == Cycles &&
		Control.AliveRenderers == Cycles &&
		Control.RenderersWithCachedFlyoutPage == 0 &&
		Control.AliveFlyoutPages == 0 &&
		Control.AlivePayloads == 0 &&
		Current.RetainedRendererPeers == Cycles &&
		Current.AliveRenderers == Cycles &&
		Current.RenderersWithCachedFlyoutPage == Cycles &&
		Current.AliveFlyoutPages == Cycles &&
		Current.AlivePayloads == Cycles;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosTabletFlyoutPageCachedFlyoutRetentionRepro",
			$"RESULT: {(Proven ? "PROVEN" : "NOT PROVEN")}",
			$"cycles={Cycles}",
			$"payloadMegabytesPerCycle={PayloadMegabytesPerCycle}",
			$"baselineManagedBytes={BaselineManagedBytes:N0}",
			$"finalManagedBytes={FinalManagedBytes:N0}",
			$"managedHeapDeltaMiB={heapDeltaMiB:N1}",
			Format(Control),
			Format(Current));
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"scenario={result.Name}",
			$"  retainedRendererPeers={result.RetainedRendererPeers}",
			$"  trackedCycles={result.TrackedCycles}",
			$"  renderersWithCachedFlyoutPage={result.RenderersWithCachedFlyoutPage}/{result.TrackedCycles}",
			$"  aliveRenderers={result.AliveRenderers}/{result.TrackedCycles}",
			$"  aliveFlyoutPages={result.AliveFlyoutPages}/{result.TrackedCycles}",
			$"  alivePayloads={result.AlivePayloads}/{result.TrackedCycles}",
			$"  retainedPayloadBytes={result.RetainedPayloadBytes:N0}",
			$"  retainedPayloadMiB={result.RetainedPayloadBytes / 1024d / 1024d:F1}");
	}
}

internal sealed class PayloadFlyoutPage : FlyoutPage
{
	public PayloadFlyoutPage(int cycle, LeakPayload payload)
	{
		Cycle = cycle;
		Title = $"Regional operations {cycle + 1}";
		AutomationId = $"tablet-flyout-renderer-payload-{cycle + 1}";
		BindingContext = payload;
		Flyout = new ContentPage
		{
			Title = $"Operations menu {cycle + 1}",
			BindingContext = payload,
			Content = new VerticalStackLayout
			{
				Children =
				{
					new Label { Text = $"Open facilities: {payload.Workspaces.Count}" },
					new Label { Text = $"Primary: {payload.Workspaces[0].Id}" }
				}
			}
		};
		Detail = new ContentPage
		{
			Title = $"Dispatch board {cycle + 1}",
			BindingContext = payload,
			Content = new Label { Text = payload.Workspaces[0].Title }
		};
	}

	public int Cycle { get; }
}

internal sealed class LeakPayload
{
	readonly byte[] _bytes;

	public LeakPayload(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		_bytes = new byte[checked((int)payloadBytes)];

		for (var i = 0; i < _bytes.Length; i += 4096)
			_bytes[i] = (byte)((cycle + i) % 251);

		Workspaces = Enumerable.Range(1, 20)
			.Select(index => new OperationsWorkspace(
				$"OPS-{cycle + 1:000}-{index:000}",
				$"Regional facility {index}",
				$"Route filters, chart caches, and approval state {cycle + 1}.{index}"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public IReadOnlyList<OperationsWorkspace> Workspaces { get; }
}

internal sealed record OperationsWorkspace(string Id, string Title, string UiState);
