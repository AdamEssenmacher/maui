using System.Reflection;
using System.Runtime.CompilerServices;
using CoreGraphics;
using Foundation;
using UIKit;

namespace ContextActionsCellGlobalCloserRetentionRepro;

#pragma warning disable CS0618

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;
	const string GlobalCloserTypeName = "GlobalCloseContextGestureRecognizer";

	static readonly ConditionalWeakTable<UIScrollView, LeakPayload> ScrollPayloads = new();

	static readonly Type ContextActionsCellType = typeof(ListView).Assembly.GetType(
		"Microsoft.Maui.Controls.Handlers.Compatibility.ContextActionsCell",
		throwOnError: true)!;

	static readonly Type ContextScrollViewDelegateType = typeof(ListView).Assembly.GetType(
		"Microsoft.Maui.Controls.Handlers.Compatibility.ContextScrollViewDelegate",
		throwOnError: true)!;

	static readonly MethodInfo UpdateMethod = ContextActionsCellType.GetMethod(
		"Update",
		BindingFlags.Instance | BindingFlags.Public)!;

	static readonly FieldInfo ScrollerField = ContextActionsCellType.GetField(
		"_scroller",
		BindingFlags.Instance | BindingFlags.NonPublic)!;

	static readonly FieldInfo ButtonsField = ContextActionsCellType.GetField(
		"_buttons",
		BindingFlags.Instance | BindingFlags.NonPublic)!;

	static readonly MethodInfo WillEndDraggingMethod = ContextScrollViewDelegateType.GetMethod(
		"WillEndDragging",
		BindingFlags.Instance | BindingFlags.Public)!;

	static readonly MethodInfo UnhookMethod = ContextScrollViewDelegateType.GetMethod(
		"Unhook",
		BindingFlags.Instance | BindingFlags.Public)!;

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunScenario(
			"control: open context actions, explicitly unhook closer recognizers, then dispose",
			unhookBeforeDispose: true);

		var current = RunScenario(
			"current: open context actions, dispose without unhooking closer recognizers",
			unhookBeforeDispose: false);

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

	static ScenarioResult RunScenario(string name, bool unhookBeforeDispose)
	{
		var retainedGlobalClosers = new List<UIGestureRecognizer>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateCycle(i, retainedGlobalClosers, tracked, unhookBeforeDispose);

		ForceFullGc();

		var result = ScenarioResult.From(name, tracked, retainedGlobalClosers);
		GC.KeepAlive(retainedGlobalClosers);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateCycle(
		int cycle,
		List<UIGestureRecognizer> retainedGlobalClosers,
		List<TrackedCycle> tracked,
		bool unhookBeforeDispose)
	{
		using var pool = new NSAutoreleasePool();
		var tableView = new UITableView(new CGRect(0, 0, 360, 52));
		var cell = CreateCell(cycle);
		var nativeCell = new PayloadTableCell(cycle);
		var contextCell = (UITableViewCell)Activator.CreateInstance(ContextActionsCellType)!;

		tableView.AddSubview(contextCell);
		contextCell.Frame = new CGRect(0, 0, 360, 52);
		contextCell.ContentView.Frame = new CGRect(0, 0, 360, 52);

		UpdateMethod.Invoke(contextCell, new object[] { tableView, cell, nativeCell });
		var scroller = (UIScrollView)ScrollerField.GetValue(contextCell)!;
		var scrollDelegate = scroller.Delegate!;
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		ScrollPayloads.Add(scroller, payload);

		OpenContextActions(scroller, scrollDelegate);
		var globalCloser = GetGlobalCloserRecognizer(tableView);

		if (unhookBeforeDispose)
		{
			UnhookMethod.Invoke(scrollDelegate, new object[] { scroller });
			globalCloser = null;
		}
		else
		{
			tableView.RemoveGestureRecognizer(globalCloser!);
			retainedGlobalClosers.Add(globalCloser!);
		}

		ClearButtonTargetActions(contextCell);

		tracked.Add(new TrackedCycle(
			cycle,
			globalCloser is null ? null : new WeakReference(globalCloser),
			new WeakReference(scroller),
			new WeakReference(contextCell),
			new WeakReference(nativeCell),
			new WeakReference(payload),
			payload.PayloadBytes));

		contextCell.RemoveFromSuperview();
		RemoveTableSubviews(tableView);
		nativeCell.Dispose();
		tableView.Dispose();
	}

	static void OpenContextActions(UIScrollView scroller, object scrollDelegate)
	{
		var invokeArgs = new object[]
		{
			scroller,
			CGPoint.Empty,
			new CGPoint(360, 0)
		};

		WillEndDraggingMethod.Invoke(scrollDelegate, invokeArgs);
	}

	static void RemoveTableSubviews(UITableView tableView)
	{
		foreach (var subview in tableView.Subviews.ToArray())
			subview.RemoveFromSuperview();
	}

	static UIGestureRecognizer? GetGlobalCloserRecognizer(UIView tableView)
	{
		return tableView.GestureRecognizers?.FirstOrDefault(gesture => gesture.GetType().Name == GlobalCloserTypeName);
	}

	static void ClearButtonTargetActions(UITableViewCell contextCell)
	{
		if (ButtonsField.GetValue(contextCell) is not IEnumerable<UIButton> buttons)
			return;

		foreach (var button in buttons)
			button.RemoveTarget(null, null, UIControlEvent.AllEvents);
	}

	static TextCell CreateCell(int cycle)
	{
		var cell = new TextCell
		{
			Text = $"Offline order {cycle + 1:000}",
			Detail = "Disposed opened row with context actions"
		};

		cell.ContextActions.Add(new MenuItem
		{
			Text = "Archive",
			Command = new Command(() => { })
		});

		return cell;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	internal sealed class PayloadTableCell : UITableViewCell
	{
		public PayloadTableCell(int cycle) : base(UITableViewCellStyle.Default, "payload")
		{
			Cycle = cycle;
		}

		public int Cycle { get; }
	}

	internal sealed class LeakPayload
	{
		public LeakPayload(int cycle, long payloadBytes)
		{
			Cycle = cycle;
			PayloadBytes = payloadBytes;
			DocumentBytes = new byte[payloadBytes];

			for (var i = 0; i < DocumentBytes.Length; i += 4096)
				DocumentBytes[i] = (byte)(cycle + i);

			CachedOrders = Enumerable.Range(1, 40)
				.Select(index => new CachedOrder(
					$"ORDER-{cycle + 1:000}-{index:000}",
					$"Offline fulfillment packet {index}",
					"Ready for sync"))
				.ToArray();
		}

		public int Cycle { get; }

		public long PayloadBytes { get; }

		public byte[] DocumentBytes { get; }

		public IReadOnlyList<CachedOrder> CachedOrders { get; }
	}

	internal sealed record CachedOrder(string Id, string Summary, string Status);

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference? GlobalCloser,
		WeakReference Scroller,
		WeakReference ContextActionCell,
		WeakReference NativeCell,
		WeakReference Payload,
		long PayloadBytes);

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedGlobalCloserRoots,
		int AliveGlobalCloserRecognizers,
		int AliveScrollViews,
		int AliveContextActionCells,
		int AliveNativeCells,
		int AlivePayloads,
		long RetainedPayloadBytes)
	{
		public static ScenarioResult From(
			string name,
			IReadOnlyList<TrackedCycle> cycles,
			IReadOnlyList<UIGestureRecognizer> retainedGlobalClosers)
		{
			var aliveGlobalCloserRecognizers = 0;
			var aliveScrollViews = 0;
			var aliveContextActionCells = 0;
			var aliveNativeCells = 0;
			var alivePayloads = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in cycles)
			{
				if (cycle.GlobalCloser?.IsAlive == true)
					aliveGlobalCloserRecognizers++;

				if (cycle.Scroller.IsAlive)
					aliveScrollViews++;

				if (cycle.ContextActionCell.IsAlive)
					aliveContextActionCells++;

				if (cycle.NativeCell.IsAlive)
					aliveNativeCells++;

				if (cycle.Payload.IsAlive)
				{
					alivePayloads++;
					retainedPayloadBytes += cycle.PayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				cycles.Count,
				retainedGlobalClosers.Count,
				aliveGlobalCloserRecognizers,
				aliveScrollViews,
				aliveContextActionCells,
				aliveNativeCells,
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
		public bool LeakProved =>
			Control.RetainedGlobalCloserRoots == 0 &&
			Control.AliveGlobalCloserRecognizers == 0 &&
			Control.AliveScrollViews == 0 &&
			Control.AlivePayloads == 0 &&
			Current.RetainedGlobalCloserRoots == Cycles &&
			Current.AliveGlobalCloserRecognizers == Cycles &&
			Current.AliveScrollViews == Cycles &&
			Current.AlivePayloads == Cycles;

		public string ToText()
		{
			return string.Join(Environment.NewLine,
				"ContextActionsCell global closer retention repro",
				$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}",
				$"cycles={Cycles}",
				$"payloadMegabytesPerCycle={PayloadMegabytesPerCycle}",
				FormatScenario(Control),
				FormatScenario(Current));
		}

		static string FormatScenario(ScenarioResult result)
		{
			return string.Join(Environment.NewLine,
				$"scenario={result.Name}",
				$"  trackedCycles={result.TrackedCycles}",
				$"  retainedGlobalCloserRoots={result.RetainedGlobalCloserRoots}",
				$"  aliveGlobalCloserRecognizers={result.AliveGlobalCloserRecognizers}",
				$"  aliveScrollViews={result.AliveScrollViews}/{result.TrackedCycles}",
				$"  aliveContextActionCells={result.AliveContextActionCells}/{result.TrackedCycles}",
				$"  aliveNativeCells={result.AliveNativeCells}/{result.TrackedCycles}",
				$"  alivePayloads={result.AlivePayloads}/{result.TrackedCycles}",
				$"  retainedPayloadBytes={result.RetainedPayloadBytes}",
				$"  retainedPayloadMiB={result.RetainedPayloadBytes / 1024d / 1024d:F1}");
		}
	}
}

#pragma warning restore CS0618
