using Microsoft.Maui.Controls;

namespace PageInternalChildrenClearRetentionRepro;

public static class PageInternalChildrenClearRetentionProbe
{
	const int Iterations = 96;
	const int PayloadBytes = 1024 * 1024;

	public static ProbeResult Run()
	{
		var before = GC.GetTotalMemory(true);

		var removeAt = RunScenario(useClear: false);
		var clear = RunScenario(useClear: true);

		ForceFullCollection();

		var after = GC.GetTotalMemory(true);

		return new ProbeResult(
			Iterations,
			PayloadBytes,
			removeAt,
			clear,
			after - before);
	}

	static ScenarioResult RunScenario(bool useClear)
	{
		var pages = new List<ContentPage>(Iterations);
		var childRefs = new List<WeakReference>(Iterations);
		var payloadRefs = new List<WeakReference>(Iterations);
		var payloadBufferRefs = new List<WeakReference>(Iterations);

		BuildScenario(useClear, pages, childRefs, payloadRefs, payloadBufferRefs);

		ForceFullCollection();

		var retainedChildren = CountAlive(childRefs);
		var retainedPayloads = CountAlive(payloadRefs);
		var retainedPayloadBuffers = CountAlive(payloadBufferRefs);
		var logicalChildren = pages.Sum(page => ((IElementController)page).LogicalChildren.Count);
		var internalChildren = pages.Sum(page => page.InternalChildren.Count);

		GC.KeepAlive(pages);

		return new ScenarioResult(
			retainedChildren,
			retainedPayloads,
			retainedPayloadBuffers,
			logicalChildren,
			internalChildren,
			retainedPayloadBuffers * PayloadBytes);
	}

	static void BuildScenario(
		bool useClear,
		List<ContentPage> pages,
		List<WeakReference> childRefs,
		List<WeakReference> payloadRefs,
		List<WeakReference> payloadBufferRefs)
	{
		for (var i = 0; i < Iterations; i++)
		{
			var page = new ContentPage();
			var payload = new Payload(i, PayloadBytes);
			var child = new Grid
			{
				BindingContext = payload
			};

			page.InternalChildren.Add(child);

			pages.Add(page);
			childRefs.Add(new WeakReference(child));
			payloadRefs.Add(new WeakReference(payload));
			payloadBufferRefs.Add(new WeakReference(payload.Buffer));

			if (useClear)
			{
				page.InternalChildren.Clear();
			}
			else
			{
				page.InternalChildren.RemoveAt(page.InternalChildren.Count - 1);
			}
		}
	}

	static int CountAlive(List<WeakReference> references)
	{
		var count = 0;

		foreach (var reference in references)
		{
			if (reference.IsAlive)
				count++;
		}

		return count;
	}

	static void ForceFullCollection()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	sealed class Payload
	{
		public Payload(int id, int payloadBytes)
		{
			Id = id;
			Buffer = new byte[payloadBytes];
		}

		public int Id { get; }

		public byte[] Buffer { get; }
	}
}

public sealed record ScenarioResult(
	int RetainedChildren,
	int RetainedPayloads,
	int RetainedPayloadBuffers,
	int LogicalChildren,
	int InternalChildren,
	long RetainedPayloadBytes);

public sealed record ProbeResult(
	int Iterations,
	int PayloadBytes,
	ScenarioResult RemoveAtControl,
	ScenarioResult ClearCurrent,
	long ManagedHeapDeltaBytes)
{
	public bool Proven =>
		RemoveAtControl.RetainedPayloadBuffers == 0 &&
		RemoveAtControl.LogicalChildren == 0 &&
		ClearCurrent.RetainedPayloadBuffers == Iterations &&
		ClearCurrent.LogicalChildren == Iterations;

	public string ToReport()
	{
		return string.Join(Environment.NewLine, new[]
		{
			"Page InternalChildren Clear Retention Repro",
			$"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}",
			$"Iterations: {Iterations}",
			$"Payload bytes per child: {PayloadBytes}",
			"",
			"RemoveAt control:",
			$"  retained children: {RemoveAtControl.RetainedChildren}/{Iterations}",
			$"  retained payloads: {RemoveAtControl.RetainedPayloads}/{Iterations}",
			$"  retained payload buffers: {RemoveAtControl.RetainedPayloadBuffers}/{Iterations}",
			$"  logical children retained by live pages: {RemoveAtControl.LogicalChildren}",
			$"  internal children retained by live pages: {RemoveAtControl.InternalChildren}",
			$"  retained payload bytes: {RemoveAtControl.RetainedPayloadBytes}",
			"",
			"InternalChildren.Clear current MAUI:",
			$"  retained children: {ClearCurrent.RetainedChildren}/{Iterations}",
			$"  retained payloads: {ClearCurrent.RetainedPayloads}/{Iterations}",
			$"  retained payload buffers: {ClearCurrent.RetainedPayloadBuffers}/{Iterations}",
			$"  logical children retained by live pages: {ClearCurrent.LogicalChildren}",
			$"  internal children retained by live pages: {ClearCurrent.InternalChildren}",
			$"  retained payload bytes: {ClearCurrent.RetainedPayloadBytes}",
			"",
			$"Managed heap delta bytes: {ManagedHeapDeltaBytes}"
		});
	}
}
