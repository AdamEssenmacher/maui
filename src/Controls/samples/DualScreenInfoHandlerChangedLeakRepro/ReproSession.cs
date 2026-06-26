using System.ComponentModel;
using System.Reflection;
using Microsoft.Maui.Foldable;

namespace DualScreenInfoHandlerChangedLeakRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	static readonly FieldInfo HandlerChangedField =
		typeof(Element).GetField("HandlerChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunControl();
		var leak = RunLeak();

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			leak);
	}

	static ScenarioResult RunControl()
	{
		var retainedElements = new List<VisualElement>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(retainedElements, tracked, i);

		ForceFullGc();
		var result = ScenarioResult.From("control: retained visual elements without DualScreenInfo observers", retainedElements, tracked);
		GC.KeepAlive(retainedElements);
		return result;
	}

	static ScenarioResult RunLeak()
	{
		var retainedElements = new List<VisualElement>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakCycle(retainedElements, tracked, i);

		ForceFullGc();
		var result = ScenarioResult.From("leak: dropped DualScreenInfo observers retained by VisualElement.HandlerChanged", retainedElements, tracked);
		GC.KeepAlive(retainedElements);
		return result;
	}

	static void CreateControlCycle(List<VisualElement> retainedElements, List<TrackedCycle> tracked, int cycle)
	{
		var element = CreateRetainedElement(cycle);
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);

		retainedElements.Add(element);
		tracked.Add(TrackedCycle.Create(cycle, null, payload, payload));
	}

	static void CreateLeakCycle(List<VisualElement> retainedElements, List<TrackedCycle> tracked, int cycle)
	{
		var element = CreateRetainedElement(cycle);
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var observer = new DualScreenInfo(element);

		observer.PropertyChanged += payload.OnDualScreenInfoChanged;

		retainedElements.Add(element);
		tracked.Add(TrackedCycle.Create(cycle, observer, payload, payload));
	}

	static VisualElement CreateRetainedElement(int cycle)
	{
		return new Grid
		{
			BindingContext = new RetainedElementViewModel(cycle),
			Children =
			{
				new Label { Text = $"Long-lived dashboard host {cycle + 1}" }
			}
		};
	}

	static int CountHandlerChangedSubscriptions(VisualElement element)
	{
		return HandlerChangedField.GetValue(element) is MulticastDelegate multicastDelegate
			? multicastDelegate.GetInvocationList().Length
			: 0;
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

	internal sealed class LeakPayload
	{
		public LeakPayload(int cycle, long payloadBytes)
		{
			Cycle = cycle;
			PayloadBytes = payloadBytes;
			WorkspaceBytes = new byte[payloadBytes];

			for (var i = 0; i < WorkspaceBytes.Length; i += 4096)
				WorkspaceBytes[i] = (byte)(cycle + i);

			OpenDocuments = Enumerable.Range(1, 24)
				.Select(index => new FoldableDocumentState(
					$"DOC-{cycle + 1:000}-{index:000}",
					$"Customer workspace document {index}",
					"cached split-screen metrics"))
				.ToArray();
		}

		public int Cycle { get; }

		public long PayloadBytes { get; }

		public byte[] WorkspaceBytes { get; }

		public IReadOnlyList<FoldableDocumentState> OpenDocuments { get; }

		public void OnDualScreenInfoChanged(object? sender, PropertyChangedEventArgs e)
		{
		}
	}

	internal sealed record RetainedElementViewModel(int Cycle);

	internal sealed record FoldableDocumentState(string Id, string Title, string State);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference? Observer,
		WeakReference Payload,
		long PayloadBytes)
	{
		public static TrackedCycle Create(int cycle, DualScreenInfo? observer, LeakPayload payload, object payloadTarget)
		{
			return new TrackedCycle(
				cycle,
				observer is null ? null : new WeakReference(observer),
				new WeakReference(payloadTarget),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveObservers,
		int AlivePayloads,
		int HandlerChangedSubscriptions,
		long RetainedPayloadBytes)
	{
		public static ScenarioResult From(string name, IReadOnlyList<VisualElement> retainedElements, IReadOnlyList<TrackedCycle> cycles)
		{
			var aliveObservers = 0;
			var alivePayloads = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in cycles)
			{
				if (cycle.Observer?.IsAlive == true)
					aliveObservers++;

				if (cycle.Payload.IsAlive)
				{
					alivePayloads++;
					retainedPayloadBytes += cycle.PayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				cycles.Count,
				aliveObservers,
				alivePayloads,
				retainedElements.Sum(CountHandlerChangedSubscriptions),
				retainedPayloadBytes);
		}
	}

	internal sealed record ReproReport(
		int Cycles,
		int PayloadMegabytesPerCycle,
		long BaselineManagedBytes,
		long FinalManagedBytes,
		ScenarioResult Control,
		ScenarioResult Leak)
	{
		public bool LeakProved =>
			Control.AliveObservers == 0 &&
			Control.AlivePayloads == 0 &&
			Leak.AliveObservers == Leak.TrackedCycles &&
			Leak.AlivePayloads == Leak.TrackedCycles &&
			Leak.HandlerChangedSubscriptions == Control.HandlerChangedSubscriptions + Leak.TrackedCycles;

		public string ToText()
		{
			return string.Join(Environment.NewLine,
				"DualScreenInfoHandlerChangedLeakRepro",
				$"Cycles: {Cycles}",
				$"Payload per cycle: {PayloadMegabytesPerCycle} MiB",
				$"Leak proved: {LeakProved}",
				string.Empty,
				FormatScenario(Control),
				string.Empty,
				FormatScenario(Leak),
				string.Empty,
				$"Managed heap baseline: {FormatBytes(BaselineManagedBytes)}",
				$"Managed heap final: {FormatBytes(FinalManagedBytes)}",
				$"Managed heap delta: {FormatBytes(FinalManagedBytes - BaselineManagedBytes)}");
		}

		static string FormatScenario(ScenarioResult result)
		{
			var expectedPayload = result.TrackedCycles == 0 ? 0 : result.TrackedCycles * 1024L * 1024L;
			var retainedPercent = expectedPayload == 0 ? 0 : result.RetainedPayloadBytes * 100.0 / expectedPayload;

			return string.Join(Environment.NewLine,
				$"Run: {result.Name}",
				$"  tracked cycles: {result.TrackedCycles}",
				$"  DualScreenInfo observers alive after full GC: {result.AliveObservers}/{result.TrackedCycles}",
				$"  payloads alive after full GC: {result.AlivePayloads}/{result.TrackedCycles}",
				$"  retained element HandlerChanged subscriptions: {result.HandlerChangedSubscriptions}",
				$"  retained payload bytes: {FormatBytes(result.RetainedPayloadBytes)} ({retainedPercent:0.0}%)");
		}

		static string FormatBytes(long bytes)
		{
			var sign = bytes < 0 ? "-" : string.Empty;
			var value = Math.Abs(bytes);

			if (value >= 1024L * 1024L * 1024L)
				return $"{sign}{value / 1024d / 1024d / 1024d:0.0} GiB";

			if (value >= 1024L * 1024L)
				return $"{sign}{value / 1024d / 1024d:0.0} MiB";

			if (value >= 1024L)
				return $"{sign}{value / 1024d:0.0} KiB";

			return $"{sign}{value} B";
		}
	}
}
