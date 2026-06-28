using System.Reflection;
using System.Runtime.CompilerServices;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;

namespace WkWebViewRendererElementRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	static readonly FieldInfo ElementBackingField =
		typeof(WkWebViewRenderer).GetField("<Element>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
		?? throw new InvalidOperationException("Could not find WkWebViewRenderer.Element backing field.");

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "wkwebviewrenderer-element-retention-results.txt");

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

		ForceFullGc();

		return ScenarioResult.From(name, tracking.Renderers, tracking.TrackedCycles);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioTracking RunScenarioCore(bool clearElementAfterDispose)
	{
		var renderers = new List<WkWebViewRenderer>(Cycles);
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
		List<WkWebViewRenderer> renderers,
		List<TrackedCycle> tracked,
		bool clearElementAfterDispose)
	{
		using var pool = new NSAutoreleasePool();
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var webView = new PayloadWebView(cycle, payload)
		{
			Source = new HtmlWebViewSource
			{
				Html = $"<html><body><h1>Retained web workspace {cycle + 1}</h1></body></html>"
			}
		};

#pragma warning disable CS0618
		var renderer = new WkWebViewRenderer();
#pragma warning restore CS0618
		renderer.SetElement(webView);
		renderer.Dispose();

		if (clearElementAfterDispose)
			ElementBackingField.SetValue(renderer, null);

		renderers.Add(renderer);
		tracked.Add(TrackedCycle.Create(cycle, renderer, webView, payload));
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
}

sealed class PayloadWebView : WebView
{
	public PayloadWebView(int cycle, LeakPayload payload)
	{
		Cycle = cycle;
		BindingContext = payload;
		AutomationId = $"wkwebview-renderer-payload-{cycle + 1}";
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

		OpenDocuments = Enumerable.Range(1, 12)
			.Select(index => new WebWorkspaceDocument(
				$"DOC-{cycle + 1:000}-{index:000}",
				$"Customer knowledge-base document {index}",
				$"Rendered preview state {cycle + 1}.{index}"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] SessionBytes { get; }

	public IReadOnlyList<WebWorkspaceDocument> OpenDocuments { get; }
}

internal sealed record WebWorkspaceDocument(string Id, string Title, string State);

internal sealed record ScenarioTracking(
	IReadOnlyList<WkWebViewRenderer> Renderers,
	IReadOnlyList<TrackedCycle> TrackedCycles);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference Renderer,
	WeakReference WebView,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(
		int cycle,
		WkWebViewRenderer renderer,
		PayloadWebView webView,
		LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(renderer),
			new WeakReference(webView),
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
	int AliveWebViews,
	int AlivePayloads,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(
		string name,
		IReadOnlyList<WkWebViewRenderer> renderers,
		IReadOnlyList<TrackedCycle> cycles)
	{
		var renderersWithElementAssigned = 0;
		foreach (var renderer in renderers)
		{
			if (renderer.Element is not null)
				renderersWithElementAssigned++;
		}

		var aliveRenderers = 0;
		var aliveWebViews = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.Renderer.IsAlive)
				aliveRenderers++;
			if (cycle.WebView.IsAlive)
				aliveWebViews++;
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
			aliveWebViews,
			alivePayloads,
			retainedPayloadBytes);
	}
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
		Control.AliveWebViews == 0 &&
		Control.AlivePayloads == 0 &&
		Current.RetainedRendererPeers == Cycles &&
		Current.AliveRenderers == Cycles &&
		Current.RenderersWithElementAssigned == Cycles &&
		Current.AliveWebViews == Cycles &&
		Current.AlivePayloads == Cycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine, new[]
		{
			"WkWebViewRenderer Element retention repro",
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
			$"  aliveWebViews={result.AliveWebViews}/{result.TrackedCycles}",
			$"  alivePayloads={result.AlivePayloads}/{result.TrackedCycles}",
			$"  retainedPayloadBytes={result.RetainedPayloadBytes}",
			$"  retainedPayloadMiB={result.RetainedPayloadBytes / 1024d / 1024d:F1}",
		});
	}
}
