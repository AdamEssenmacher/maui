#nullable enable

using System.Runtime.CompilerServices;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using UIKit;

namespace MacCatalystContextActionsMoreActionSheetRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int MenuItemsPerCell = 8;
	internal const int PayloadBytesPerCell = 1024 * 1024;
	const string PayloadPrefix = "maccatalyst-contextactions-more-actionsheet-";

	static readonly List<RetainedActionSheet> RetainedActionSheets = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "maccatalyst-contextactions-more-actionsheet-retention-results.txt");

	public static Task<ReproReport> RunAsync(Page _page, IMauiContext _mauiContext)
	{
		RetainedActionSheets.Clear();
		WriteProgress("Starting Mac Catalyst ContextActions More action sheet retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: clear disposed ContextActionsCell.ContentCell while retaining More action sheets",
			clearContentCellOnDispose: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: disposed ContextActionsCell leaves ContentCell assigned under retained More action sheets",
			clearContentCellOnDispose: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedActionSheets);

		return Task.FromResult(new ReproReport(
			Cycles,
			MenuItemsPerCell,
			PayloadBytesPerCell,
			baselineBytes,
			finalBytes,
			control,
			current));
	}

	static ScenarioResult RunScenario(string name, bool clearContentCellOnDispose)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 12 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateCycle(i, tracked, clearContentCellOnDispose);
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateCycle(int cycle, List<TrackedCycle> tracked, bool clearContentCellOnDispose)
	{
		var payload = new Payload($"{PayloadPrefix}{cycle:D4}", PayloadBytesPerCell);
		var nativeCell = new PayloadTableCell(payload);
		var contextCell = new MirroredContextActionsCell(nativeCell);
		var menuItems = Enumerable.Range(0, MenuItemsPerCell)
			.Select(i => new MenuItem { Text = $"Action {cycle:D4}-{i:D2}" })
			.ToArray();

		var actionSheet = contextCell.CreateMoreActionSheet(menuItems);
		var retained = new RetainedActionSheet(actionSheet);
		RetainedActionSheets.Add(retained);

		contextCell.DisposeMirrored(clearContentCellOnDispose);

		tracked.Add(TrackedCycle.Create(retained, contextCell, nativeCell, payload));
	}

	internal sealed class MirroredContextActionsCell
	{
		UIScrollView? _scroller = new(new CGRect(0, 0, 320, 44));

		public MirroredContextActionsCell(UITableViewCell contentCell)
		{
			ContentCell = contentCell;
		}

		public UITableViewCell? ContentCell { get; private set; }

		public UIAlertController CreateMoreActionSheet(IReadOnlyList<MenuItem> contextActions)
		{
			var actionSheet = UIAlertController.Create("More", null, UIAlertControllerStyle.ActionSheet);

			for (var i = 0; i < contextActions.Count; i++)
			{
				var item = contextActions[i];
				var weakItem = new WeakReference<MenuItem>(item);
				var action = UIAlertAction.Create(item.Text, UIAlertActionStyle.Default, _ =>
				{
					if (_scroller == null)
						return;

					_scroller.SetContentOffset(new CGPoint(0, 0), true);
					if (weakItem.TryGetTarget(out var menuItem))
						((IMenuItemController)menuItem).Activate();
				});

				actionSheet.AddAction(action);
			}

			return actionSheet;
		}

		public void DisposeMirrored(bool clearContentCell)
		{
			_scroller?.Dispose();
			_scroller = null;

			if (clearContentCell)
				ContentCell = null;
		}
	}

	internal sealed class PayloadTableCell : UITableViewCell
	{
		readonly Payload _payload;

		public PayloadTableCell(Payload payload) : base(UITableViewCellStyle.Default, reuseIdentifier: null)
		{
			_payload = payload;
		}
	}

	internal sealed class Payload
	{
		public Payload(string name, int bytes)
		{
			Name = name;
			Bytes = new byte[bytes];
			Bytes[0] = 0x42;
			Bytes[^1] = 0x24;
		}

		public string Name { get; }
		public byte[] Bytes { get; }
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

	internal sealed record RetainedActionSheet(UIAlertController ActionSheet)
	{
		public int ActionCount => ActionSheet.Actions.Length;
	}

	internal sealed record TrackedCycle(
		RetainedActionSheet Retained,
		WeakReference<MirroredContextActionsCell> ContextCell,
		WeakReference<PayloadTableCell> NativeCell,
		WeakReference<Payload> Payload)
	{
		public static TrackedCycle Create(
			RetainedActionSheet retained,
			MirroredContextActionsCell contextCell,
			PayloadTableCell nativeCell,
			Payload payload)
		{
			return new TrackedCycle(
				retained,
				new WeakReference<MirroredContextActionsCell>(contextCell),
				new WeakReference<PayloadTableCell>(nativeCell),
				new WeakReference<Payload>(payload));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveActionSheets,
		int RetainedNativeActions,
		int AliveContextCells,
		int AliveNativeCells,
		int AlivePayloads,
		long EstimatedAlivePayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveActionSheets = 0;
			var retainedNativeActions = 0;
			var aliveContextCells = 0;
			var aliveNativeCells = 0;
			var alivePayloads = 0;
			long estimatedAlivePayloadBytes = 0;

			foreach (var item in tracked)
			{
				if (item.Retained.ActionSheet.Handle != IntPtr.Zero)
				{
					aliveActionSheets++;
					retainedNativeActions += item.Retained.ActionCount;
				}

				if (item.ContextCell.TryGetTarget(out _))
					aliveContextCells++;

				if (item.NativeCell.TryGetTarget(out _))
					aliveNativeCells++;

				if (item.Payload.TryGetTarget(out var payload) &&
					payload.Name.StartsWith(PayloadPrefix, StringComparison.Ordinal))
				{
					alivePayloads++;
					estimatedAlivePayloadBytes += payload.Bytes.LongLength;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveActionSheets,
				retainedNativeActions,
				aliveContextCells,
				aliveNativeCells,
				alivePayloads,
				estimatedAlivePayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int MenuItemsPerCell,
	int PayloadBytesPerCell,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public int ExpectedActions => Cycles * MenuItemsPerCell;

	public long ExpectedPayloadBytes => (long)Cycles * PayloadBytesPerCell;

	public bool LeakProved =>
		Control.AliveActionSheets == Cycles &&
		Current.AliveActionSheets == Cycles &&
		Control.RetainedNativeActions == ExpectedActions &&
		Current.RetainedNativeActions == ExpectedActions &&
		Control.AlivePayloads == 0 &&
		Control.AliveNativeCells == 0 &&
		Current.AliveContextCells == Cycles &&
		Current.AliveNativeCells == Cycles &&
		Current.AlivePayloads == Cycles &&
		Current.EstimatedAlivePayloadBytes >= ExpectedPayloadBytes;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"MacCatalystContextActionsMoreActionSheetRetentionRepro",
			$"Cycles: {Cycles}",
			$"Menu items per More action sheet: {MenuItemsPerCell}",
			$"Payload bytes per context-action row: {PayloadBytesPerCell:N0}",
			$"Expected retained action sheets: {Cycles}",
			$"Expected retained native actions: {ExpectedActions}",
			$"Expected payload bytes: {ExpectedPayloadBytes:N0}",
			"Source path mirrored: ContextActionsCell.ActivateMore() UIAlertController/UIAlertAction construction.",
			"Control keeps native action sheets/actions alive but clears disposed ContentCell state after construction.",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained payload: {FormatBytes(Control.EstimatedAlivePayloadBytes)}",
			$"Current estimated retained payload: {FormatBytes(Current.EstimatedAlivePayloadBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native More action sheets: {result.AliveActionSheets}/{result.TrackedCycles}",
			$"  retained native UIAlertActions: {result.RetainedNativeActions}",
			$"  alive mirrored ContextActionsCells: {result.AliveContextCells}/{result.TrackedCycles}",
			$"  alive native payload cells: {result.AliveNativeCells}/{result.TrackedCycles}",
			$"  alive row payloads: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  estimated alive payload bytes: {result.EstimatedAlivePayloadBytes:N0}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024d / 1024d:N1} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024d:N1} KiB";
		return $"{bytes:N0} B";
	}
}
