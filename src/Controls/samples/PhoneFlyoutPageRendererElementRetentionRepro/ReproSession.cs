using System.Runtime.CompilerServices;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;

namespace PhoneFlyoutPageRendererElementRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	static readonly List<IReadOnlyList<PhoneFlyoutPageRenderer>> RetainedNativeRendererPeers = new();

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "phoneflyoutpagerenderer-element-retention-results.txt");

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunScenario("control: dispose renderer and clear stale Element", clearElementAfterDispose: true);
		var current = RunScenario("current: dispose renderer with Element still assigned", clearElementAfterDispose: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(string name, bool clearElementAfterDispose)
	{
		var tracking = RunScenarioCore(clearElementAfterDispose);
		RetainedNativeRendererPeers.Add(tracking.Renderers);

		ForceFullGc();

		return ScenarioResult.From(name, tracking.Renderers, tracking.TrackedCycles);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioTracking RunScenarioCore(bool clearElementAfterDispose)
	{
		var renderers = new List<PhoneFlyoutPageRenderer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateDisposedRendererCycle(i, renderers, tracked, clearElementAfterDispose);
		}

		return new ScenarioTracking(renderers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisposedRendererCycle(
		int cycle,
		List<PhoneFlyoutPageRenderer> renderers,
		List<TrackedCycle> tracked,
		bool clearElementAfterDispose)
	{
		using var pool = new NSAutoreleasePool();
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var flyoutPage = new PayloadFlyoutPage(cycle, payload);
		var renderer = new PhoneFlyoutPageRenderer();

		renderer.SetElement(flyoutPage);
		renderer.Dispose();
		((IElementController)flyoutPage).EffectControlProvider = null;

		if (clearElementAfterDispose)
			ElementField(renderer) = null!;

		renderers.Add(renderer);
		tracked.Add(TrackedCycle.Create(cycle, renderer, flyoutPage, payload));
	}

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Element>k__BackingField")]
	static extern ref VisualElement ElementField(PhoneFlyoutPageRenderer renderer);

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}
}

sealed class PayloadFlyoutPage : FlyoutPage
{
	public PayloadFlyoutPage(int cycle, LeakPayload payload)
	{
		Cycle = cycle;
		Title = $"Regional operations {cycle + 1}";
		AutomationId = $"phone-flyout-renderer-payload-{cycle + 1}";
		BindingContext = payload;
		Flyout = new ContentPage
		{
			Title = $"Menu {cycle + 1}",
			Content = new VerticalStackLayout
			{
				Children =
				{
					new Label { Text = $"Open routes: {payload.OpenRoutes.Count}" },
					new Label { Text = $"Workspace: {payload.OpenRoutes[0].Id}" }
				}
			}
		};
		Detail = new ContentPage
		{
			Title = $"Dispatch board {cycle + 1}",
			Content = new Label { Text = payload.OpenRoutes[0].Title }
		};
	}

	public int Cycle { get; }
}

internal sealed class LeakPayload
{
	public LeakPayload(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		SessionBytes = new byte[payloadBytes];

		for (var i = 0; i < SessionBytes.Length; i += 4096)
			SessionBytes[i] = (byte)(cycle + i);

		OpenRoutes = Enumerable.Range(1, 16)
			.Select(index => new RouteWorkspace(
				$"FLY-{cycle + 1:000}-{index:000}",
				$"Field team route {index}",
				$"Flyout/detail state and filter cache {cycle + 1}.{index}"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] SessionBytes { get; }

	public IReadOnlyList<RouteWorkspace> OpenRoutes { get; }
}

internal sealed record RouteWorkspace(string Id, string Title, string UiState);

internal sealed record ScenarioTracking(
	IReadOnlyList<PhoneFlyoutPageRenderer> Renderers,
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
		PhoneFlyoutPageRenderer renderer,
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
	int RenderersWithElementAssigned,
	int AliveRenderers,
	int AliveFlyoutPages,
	int AlivePayloads,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(
		string name,
		IReadOnlyList<PhoneFlyoutPageRenderer> renderers,
		IReadOnlyList<TrackedCycle> cycles)
	{
		var renderersWithElementAssigned = 0;
		foreach (var renderer in renderers)
		{
			if (ElementField(renderer) is not null)
				renderersWithElementAssigned++;
		}

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
			renderersWithElementAssigned,
			aliveRenderers,
			aliveFlyoutPages,
			alivePayloads,
			retainedPayloadBytes);
	}

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Element>k__BackingField")]
	static extern ref VisualElement ElementField(PhoneFlyoutPageRenderer renderer);
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadMegabytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ScenarioResult Control,
	ScenarioResult Current)
{
	public bool Proven =>
		Control.RetainedRendererPeers == Cycles &&
		Control.AliveRenderers == Cycles &&
		Control.RenderersWithElementAssigned == 0 &&
		Control.AliveFlyoutPages == 0 &&
		Control.AlivePayloads == 0 &&
		Current.RetainedRendererPeers == Cycles &&
		Current.AliveRenderers == Cycles &&
		Current.RenderersWithElementAssigned == Cycles &&
		Current.AliveFlyoutPages == Cycles &&
		Current.AlivePayloads == Cycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine, new[]
		{
			"PhoneFlyoutPageRenderer Element retention repro",
			$"RESULT: {(Proven ? "PROVEN" : "NOT PROVEN")}",
			$"cycles={Cycles}",
			$"payloadMegabytesPerCycle={PayloadMegabytesPerCycle}",
			$"baselineManagedBytes={BaselineManagedBytes}",
			$"finalManagedBytes={FinalManagedBytes}",
			Format(Control),
			Format(Current),
		});
	}

	static string Format(ScenarioResult result)
	{
		return string.Join(Environment.NewLine, new[]
		{
			$"scenario={result.Name}",
			$"  retainedRendererPeers={result.RetainedRendererPeers}",
			$"  trackedCycles={result.TrackedCycles}",
			$"  renderersWithElementAssigned={result.RenderersWithElementAssigned}/{result.TrackedCycles}",
			$"  aliveRenderers={result.AliveRenderers}/{result.TrackedCycles}",
			$"  aliveFlyoutPages={result.AliveFlyoutPages}/{result.TrackedCycles}",
			$"  alivePayloads={result.AlivePayloads}/{result.TrackedCycles}",
			$"  retainedPayloadBytes={result.RetainedPayloadBytes}",
			$"  retainedPayloadMiB={result.RetainedPayloadBytes / 1024d / 1024d:F1}",
		});
	}
}
