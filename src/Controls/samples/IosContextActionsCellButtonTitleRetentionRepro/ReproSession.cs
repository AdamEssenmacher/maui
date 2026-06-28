#nullable enable

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace IosContextActionsCellButtonTitleRetentionRepro;

#pragma warning disable CS0618

internal static class ReproSession
{
	internal const int Cycles = 2048;
	internal const int PayloadKiBPerButton = 2;
	const long PayloadBytesPerButton = PayloadKiBPerButton * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedButtonPeer>> RetainedNativePeers = new();
	static readonly UIControlState[] TitleStates =
	[
		UIControlState.Normal,
		UIControlState.Highlighted,
		UIControlState.Disabled,
		UIControlState.Selected,
		UIControlState.Focused
	];

	static readonly Type ContextActionsCellType = typeof(ListView).Assembly.GetType(
		"Microsoft.Maui.Controls.Handlers.Compatibility.ContextActionsCell",
		throwOnError: true)!;

	static readonly MethodInfo UpdateMethod = ContextActionsCellType.GetMethod(
		"Update",
		BindingFlags.Instance | BindingFlags.Public)!;

	static readonly FieldInfo ButtonsField = ContextActionsCellType.GetField(
		"_buttons",
		BindingFlags.Instance | BindingFlags.NonPublic)!;

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-contextactioncell-button-title-retention-results.txt");

	public static ReproReport Run()
	{
		WriteProgress("Starting iOS ContextActionsCell button title retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: clear native context-action UIButton title slots before cell disposal",
			clearNativeTitleBeforeDispose: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: ContextActionsCell disposal leaves native UIButton title slots assigned",
			clearNativeTitleBeforeDispose: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadKiBPerButton,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(string name, bool clearNativeTitleBeforeDispose)
	{
		var retainedPeers = new List<RetainedButtonPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 256 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateCycle(i, retainedPeers, tracked, clearNativeTitleBeforeDispose);
		}

		RetainedNativePeers.Add(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, retainedPeers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateCycle(
		int cycle,
		List<RetainedButtonPeer> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeTitleBeforeDispose)
	{
		using var pool = new NSAutoreleasePool();

		var tableView = new UITableView(new CGRect(0, 0, 720, 56));
		var cell = CreateCell(cycle);
		var menuItem = cell.ContextActions[0];
		var nativeCell = new UITableViewCell(UITableViewCellStyle.Default, "payload");
		var contextCell = (UITableViewCell)Activator.CreateInstance(ContextActionsCellType)!;

		contextCell.Frame = new CGRect(0, 0, 720, 56);
		contextCell.ContentView.Frame = new CGRect(0, 0, 720, 56);
		tableView.AddSubview(contextCell);

		UpdateMethod.Invoke(contextCell, new object[] { tableView, cell, nativeCell });

		var button = GetSingleContextButton(contextCell);
		if (!NativeTitleHasPayload(button))
			throw new InvalidOperationException("ContextActionsCell did not assign the expected native button title payload.");

		var retainedPeer = RetainNativePeer(button);

		if (clearNativeTitleBeforeDispose)
			ClearNativeTitle(button);

		tracked.Add(TrackedCycle.Create(cycle, contextCell, nativeCell, cell, menuItem));

		contextCell.Dispose();
		nativeCell.Dispose();
		tableView.Dispose();

		retainedPeers.Add(retainedPeer);
	}

	static TextCell CreateCell(int cycle)
	{
		var cell = new TextCell
		{
			Text = $"Offline order {cycle + 1:000}",
			Detail = "Context action with generated workflow title"
		};

		cell.ContextActions.Add(new MenuItem
		{
			Text = CreateOperationalTitle(cycle),
			Command = new Command(() => { })
		});

		return cell;
	}

	static string CreateOperationalTitle(int cycle)
	{
		var header = $"Cycle {cycle:0000} offline action. ";
		var sentence = "Archive synced customer records, policy notes, and fulfillment state for later review. ";
		var targetChars = (int)(PayloadBytesPerButton / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static UIButton GetSingleContextButton(UITableViewCell contextCell)
	{
		if (ButtonsField.GetValue(contextCell) is not IReadOnlyList<UIButton> buttons || buttons.Count != 1)
			throw new InvalidOperationException("Expected ContextActionsCell to create exactly one native action button.");

		return buttons[0];
	}

	static bool NativeTitleHasPayload(UIButton button) =>
		EstimateButtonTitleBytes(button) >= PayloadBytesPerButton * 0.95;

	static void ClearNativeTitle(UIButton button)
	{
		foreach (var state in TitleStates)
		{
			button.SetTitle(null, state);
			button.SetAttributedTitle(null, state);
		}

		if (button.TitleLabel is { } titleLabel)
		{
			titleLabel.Text = null;
			titleLabel.AttributedText = null;
		}
	}

	static RetainedButtonPeer RetainNativePeer(UIButton button)
	{
		var handle = button.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UIButton peer with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedButtonPeer(retained);
	}

	static NativeTitleSnapshot GetNativeTitleSnapshot(RetainedButtonPeer retainedPeer)
	{
		var peer = retainedPeer.TryGetPeer();
		if (peer is null)
			return new NativeTitleSnapshot(Alive: false, EstimatedTitleBytes: 0);

		return new NativeTitleSnapshot(Alive: true, EstimateButtonTitleBytes(peer));
	}

	static long EstimateButtonTitleBytes(UIButton button)
	{
		long maxStateBytes = 0;

		foreach (var state in TitleStates)
		{
			var stateBytes = EstimateTextBytes(
				button.Title(state),
				button.GetAttributedTitle(state)?.Value);
			maxStateBytes = Math.Max(maxStateBytes, stateBytes);
		}

		var labelBytes = button.TitleLabel is null
			? 0
			: EstimateTextBytes(button.TitleLabel.Text, button.TitleLabel.AttributedText?.Value);

		return Math.Max(maxStateBytes, labelBytes);
	}

	static long EstimateTextBytes(string? text, string? attributedText)
	{
		var retainedText = attributedText ?? text;
		return string.IsNullOrEmpty(retainedText) ? 0 : retainedText.Length * 2L;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(50);
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

	[DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

	internal sealed record NativeTitleSnapshot(bool Alive, long EstimatedTitleBytes);

	internal sealed class RetainedButtonPeer
	{
		public RetainedButtonPeer(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UIButton? TryGetPeer()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UIButton>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<object> ContextActionCell,
		WeakReference<object> NativeCell,
		WeakReference<object> Cell,
		WeakReference<object> MenuItem)
	{
		public static TrackedCycle Create(int cycle, object contextActionCell, object nativeCell, object cell, object menuItem)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<object>(contextActionCell),
				new WeakReference<object>(nativeCell),
				new WeakReference<object>(cell),
				new WeakReference<object>(menuItem));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeButtons,
		int NativeButtonsWithTitle,
		long EstimatedNativeTitleBytes,
		int AliveContextActionCells,
		int AliveNativeCells,
		int AliveCells,
		int AliveMenuItems)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedButtonPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeButtons = 0;
			var nativeButtonsWithTitle = 0;
			long estimatedNativeTitleBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				var snapshot = GetNativeTitleSnapshot(retainedPeer);
				if (!snapshot.Alive)
					continue;

				retainedNativeButtons++;
				if (snapshot.EstimatedTitleBytes > 0)
				{
					nativeButtonsWithTitle++;
					estimatedNativeTitleBytes += Math.Min(snapshot.EstimatedTitleBytes, PayloadBytesPerButton);
				}
			}

			var aliveContextActionCells = 0;
			var aliveNativeCells = 0;
			var aliveCells = 0;
			var aliveMenuItems = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.ContextActionCell.TryGetTarget(out _))
					aliveContextActionCells++;

				if (cycle.NativeCell.TryGetTarget(out _))
					aliveNativeCells++;

				if (cycle.Cell.TryGetTarget(out _))
					aliveCells++;

				if (cycle.MenuItem.TryGetTarget(out _))
					aliveMenuItems++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativeButtons,
				nativeButtonsWithTitle,
				estimatedNativeTitleBytes,
				aliveContextActionCells,
				aliveNativeCells,
				aliveCells,
				aliveMenuItems);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerButton,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeButtons == Cycles &&
		Control.NativeButtonsWithTitle == 0 &&
		Control.AliveContextActionCells <= 1 &&
		Control.AliveNativeCells <= 1 &&
		Control.AliveCells <= 1 &&
		Control.AliveMenuItems <= 1 &&
		Current.RetainedNativeButtons == Cycles &&
		Current.NativeButtonsWithTitle == Cycles &&
		Current.EstimatedNativeTitleBytes >= Cycles * PayloadKiBPerButton * 1024L * 0.95 &&
		Current.AliveContextActionCells <= 1 &&
		Current.AliveNativeCells <= 1 &&
		Current.AliveCells <= 1 &&
		Current.AliveMenuItems <= 1;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedNativeTitleBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedNativeTitleBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosContextActionsCellButtonTitleRetentionRepro",
			$"ContextActionsCell cycles per scenario: {Cycles}",
			$"Payload per native action button title: {PayloadKiBPerButton} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native title payload: {controlMiB:N1} MiB",
			$"Current estimated retained native title payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var retainedMiB = result.EstimatedNativeTitleBytes / 1024d / 1024d;
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native UIButton peers: {result.RetainedNativeButtons}/{result.TrackedCycles}",
			$"  native buttons with assigned title: {result.NativeButtonsWithTitle}/{result.TrackedCycles}",
			$"  estimated retained native title bytes: {result.EstimatedNativeTitleBytes:N0}",
			$"  estimated retained native title MiB: {retainedMiB:N1}",
			$"  alive ContextActionsCells: {result.AliveContextActionCells}/{result.TrackedCycles}",
			$"  alive native payload cells: {result.AliveNativeCells}/{result.TrackedCycles}",
			$"  alive MAUI cells: {result.AliveCells}/{result.TrackedCycles}",
			$"  alive MenuItems: {result.AliveMenuItems}/{result.TrackedCycles}");
	}
}
