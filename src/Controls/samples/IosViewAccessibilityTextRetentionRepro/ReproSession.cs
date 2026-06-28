#nullable enable

using System.Text;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace IosViewAccessibilityTextRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerTextSlot = 128;
	internal const int TextSlotsPerCycle = 3;

	const long PayloadBytesPerTextSlot = PayloadKiBPerTextSlot * 1024L;

	static readonly List<RetainedView> RetainedViews = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-view-accessibility-text-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS view accessibility text retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			context,
			"control: clear retained UIView accessibility text slots",
			clearNativeText: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			context,
			"current: view handler disconnect leaves accessibility text assigned",
			clearNativeText: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(context);
		GC.KeepAlive(RetainedViews);

		return new ReproReport(
			Cycles,
			PayloadKiBPerTextSlot,
			TextSlotsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(IMauiContext context, string name, bool clearNativeText)
	{
		var retainedViews = new List<RetainedView>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(context, i, clearNativeText);
			retainedViews.Add(cycleResult.RetainedView);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedViews.AddRange(retainedViews);
		ForceFullGc();

		return ScenarioResult.From(name, retainedViews, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(IMauiContext context, int cycle, bool clearNativeText)
	{
		var automationId = CreateLargeText("automation-id", cycle);
		var description = CreateLargeText("semantic-description", cycle);
		var hint = CreateLargeText("semantic-hint", cycle);

		var view = new BoxView
		{
			AutomationId = automationId,
			WidthRequest = 24,
			HeightRequest = 24
		};
		SemanticProperties.SetDescription(view, description);
		SemanticProperties.SetHint(view, hint);

		var platformView = view.ToPlatform(context) as UIView
			?? throw new InvalidOperationException("BoxView did not create a UIView platform view.");

		if (EstimateTextBytes(platformView.AccessibilityIdentifier) < PayloadBytesPerTextSlot * 0.95)
			throw new InvalidOperationException("AutomationId was not assigned to UIView.AccessibilityIdentifier.");

		if (EstimateTextBytes(platformView.AccessibilityLabel) < PayloadBytesPerTextSlot * 0.95)
			throw new InvalidOperationException("Semantic description was not assigned to UIView.AccessibilityLabel.");

		if (EstimateTextBytes(platformView.AccessibilityHint) < PayloadBytesPerTextSlot * 0.95)
			throw new InvalidOperationException("Semantic hint was not assigned to UIView.AccessibilityHint.");

		var handler = view.Handler as IElementHandler
			?? throw new InvalidOperationException("BoxView did not keep an element handler.");

		if (clearNativeText)
			ClearNativeText(platformView);

		var tracked = TrackedCycle.Create(cycle, view, handler);
		handler.DisconnectHandler();
		await DrainMainQueueAsync();

		GC.KeepAlive(platformView);

		return new CycleResult(
			new RetainedView(platformView),
			tracked);
	}

	static void ClearNativeText(UIView platformView)
	{
		platformView.AccessibilityIdentifier = null;
		platformView.AccessibilityLabel = null;
		platformView.AccessibilityHint = null;
	}

	static string CreateLargeText(string prefix, int cycle)
	{
		var header = $"{prefix} {cycle:000}. ";
		var sentence = "Generated accessible summary, imported workflow context, offline case note, and guided operation hint. ";
		var targetChars = (int)(PayloadBytesPerTextSlot / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static async Task DrainMainQueueAsync()
	{
		await Task.Delay(30);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(75);
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

	static int CountAssignedPayloadTextSlots(UIView platformView)
	{
		var slots = 0;

		if (EstimateTextBytes(platformView.AccessibilityIdentifier) >= PayloadBytesPerTextSlot * 0.95)
			slots++;

		if (EstimateTextBytes(platformView.AccessibilityLabel) >= PayloadBytesPerTextSlot * 0.95)
			slots++;

		if (EstimateTextBytes(platformView.AccessibilityHint) >= PayloadBytesPerTextSlot * 0.95)
			slots++;

		return slots;
	}

	static long EstimateAssignedTextBytes(UIView platformView)
	{
		return
			Math.Min(EstimateTextBytes(platformView.AccessibilityIdentifier), PayloadBytesPerTextSlot) +
			Math.Min(EstimateTextBytes(platformView.AccessibilityLabel), PayloadBytesPerTextSlot) +
			Math.Min(EstimateTextBytes(platformView.AccessibilityHint), PayloadBytesPerTextSlot);
	}

	static long EstimateTextBytes(string? text)
	{
		return string.IsNullOrEmpty(text) ? 0 : text.Length * 2L;
	}

	internal sealed record RetainedView(UIView PlatformView);

	sealed record CycleResult(RetainedView RetainedView, TrackedCycle Tracked);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<BoxView> View,
		WeakReference<IElementHandler> Handler)
	{
		public static TrackedCycle Create(int cycle, BoxView view, IElementHandler handler)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<BoxView>(view),
				new WeakReference<IElementHandler>(handler));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeViews,
		int AssignedPayloadTextSlots,
		long EstimatedAssignedTextBytes,
		int AliveViews,
		int AliveHandlers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedView> retainedViews,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var assignedPayloadTextSlots = 0;
			long estimatedAssignedTextBytes = 0;

			foreach (var retainedView in retainedViews)
			{
				assignedPayloadTextSlots += CountAssignedPayloadTextSlots(retainedView.PlatformView);
				estimatedAssignedTextBytes += EstimateAssignedTextBytes(retainedView.PlatformView);
			}

			var aliveViews = 0;
			var aliveHandlers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.View.TryGetTarget(out _))
					aliveViews++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedViews.Count,
				assignedPayloadTextSlots,
				estimatedAssignedTextBytes,
				aliveViews,
				aliveHandlers);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerTextSlot,
	int TextSlotsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved
	{
		get
		{
			var expectedSlots = Cycles * TextSlotsPerCycle;
			return
				Control.RetainedNativeViews == Cycles &&
				Control.AssignedPayloadTextSlots == 0 &&
				Current.RetainedNativeViews == Cycles &&
				Current.AssignedPayloadTextSlots == expectedSlots &&
				Current.EstimatedAssignedTextBytes >= expectedSlots * PayloadKiBPerTextSlot * 1024L * 0.95 &&
				Current.AliveViews <= 1 &&
				Current.AliveHandlers <= 1;
		}
	}

	public string ToText()
	{
		var retainedMiB = Current.EstimatedAssignedTextBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedAssignedTextBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosViewAccessibilityTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Text slots per cycle: {TextSlotsPerCycle}",
			$"Payload per native accessibility text slot: {PayloadKiBPerTextSlot} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native accessibility text payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native accessibility text payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var nativeTextMiB = result.EstimatedAssignedTextBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native views: {result.RetainedNativeViews}/{result.TrackedCycles}",
			$"  assigned payload-sized text slots: {result.AssignedPayloadTextSlots}/{result.TrackedCycles * 3}",
			$"  estimated assigned native accessibility text bytes: {result.EstimatedAssignedTextBytes:N0}",
			$"  estimated assigned native accessibility text MiB: {nativeTextMiB:N1}",
			$"  alive BoxViews: {result.AliveViews}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}");
	}
}
