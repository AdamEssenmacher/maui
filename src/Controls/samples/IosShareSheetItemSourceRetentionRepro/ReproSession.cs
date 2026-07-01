#nullable enable

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Foundation;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using ObjCRuntime;
using UIKit;

namespace IosShareSheetItemSourceRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 80;
	internal const int PayloadKiBPerShareText = 1024;
	const long PayloadBytesPerShareText = PayloadKiBPerShareText * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedActivityPeer>> RetainedActivityControllers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-share-sheet-itemsource-retention-results.txt");

	public static ReproReport Run()
	{
		WriteProgress("Starting iOS/Mac Catalyst Share sheet item-source retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: retained UIActivityViewController peers after explicit item-source payload cleanup",
			createMauiItemSource: false,
			clearPayloadAfterControllerCreation: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: retained UIActivityViewController peers keep MAUI ShareActivityItemSource payloads",
			createMauiItemSource: true,
			clearPayloadAfterControllerCreation: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedActivityControllers);

		return new ReproReport(
			Cycles,
			PayloadKiBPerShareText,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(
		string name,
		bool createMauiItemSource,
		bool clearPayloadAfterControllerCreation)
	{
		var retainedControllers = new List<RetainedActivityPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 20 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateShareCycle(
				i,
				createMauiItemSource,
				clearPayloadAfterControllerCreation,
				retainedControllers,
				tracked);
		}

		RetainedActivityControllers.Add(retainedControllers);
		ForceFullGc();

		return ScenarioResult.From(name, retainedControllers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateShareCycle(
		int cycle,
		bool createMauiItemSource,
		bool clearPayloadAfterControllerCreation,
		List<RetainedActivityPeer> retainedControllers,
		List<TrackedCycle> tracked)
	{
		CreateShareCycleCore(
			cycle,
			createMauiItemSource,
			clearPayloadAfterControllerCreation,
			retainedControllers,
			tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateShareCycleCore(
		int cycle,
		bool createMauiItemSource,
		bool clearPayloadAfterControllerCreation,
		List<RetainedActivityPeer> retainedControllers,
		List<TrackedCycle> tracked)
	{
		using var pool = new NSAutoreleasePool();

		var payloadText = CreatePayload(cycle);
		var item = new NSString(payloadText);
		var title = $"Quarterly field-service audit export {cycle:000}";

		var itemSource = createMauiItemSource
			? CreateMauiShareActivityItemSource(item, title)
			: new ClearableShareActivityItemSource(item, title);

		using var activityController = new UIActivityViewController([itemSource], null)
		{
			CompletionWithItemsHandler = (_, _, _, _) =>
			{
			}
		};

		var retainedController = RetainActivityController(activityController);

		if (clearPayloadAfterControllerCreation && itemSource is ClearableShareActivityItemSource clearable)
			clearable.ClearPayload();

		retainedControllers.Add(retainedController);
		tracked.Add(TrackedCycle.Create(activityController, itemSource, item, payloadText));
	}

	static UIActivityItemSource CreateMauiShareActivityItemSource(NSString item, string title)
	{
		var type = typeof(Share).Assembly.GetType("Microsoft.Maui.ApplicationModel.DataTransfer.ShareActivityItemSource")
			?? throw new InvalidOperationException("Could not locate MAUI ShareActivityItemSource.");

		return (UIActivityItemSource)(Activator.CreateInstance(
			type,
			BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
			binder: null,
			args: [item, title],
			culture: null)
			?? throw new InvalidOperationException("Could not create MAUI ShareActivityItemSource."));
	}

	static string CreatePayload(int cycle)
	{
		var targetChars = (int)(PayloadBytesPerShareText / 2);
		var sentence =
			$"share export row {cycle:0000}: customer route notes, labor entries, invoice exceptions, photos index, and dispatch audit metadata. ";
		var builder = new StringBuilder(targetChars + sentence.Length);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static RetainedActivityPeer RetainActivityController(UIActivityViewController controller)
	{
		var handle = controller.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a UIActivityViewController with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedActivityPeer(retained);
	}

	static PayloadSnapshot GetPayloadSnapshot(object source)
	{
		if (source is ClearableShareActivityItemSource clearable)
			return clearable.GetSnapshot();

		var type = source.GetType();
		var item = type.GetField("item", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(source) as NSString;
		var title = type.GetField("title", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(source) as string;

		var itemBytes = EstimateBytes(item);
		var titleBytes = EstimateBytes(title);
		return new PayloadSnapshot(
			HasPayload: itemBytes >= PayloadBytesPerShareText * 0.95,
			EstimatedPayloadBytes: itemBytes + titleBytes);
	}

	static long EstimateBytes(NSString? value) =>
		value is null ? 0 : (long)value.Length * 2L;

	static long EstimateBytes(string? value) =>
		string.IsNullOrEmpty(value) ? 0 : value.Length * 2L;

	static void ForceFullGc()
	{
		for (var i = 0; i < 5; i++)
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

	[DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

	internal sealed class ClearableShareActivityItemSource(NSString item, string title) : UIActivityItemSource
	{
		NSString? _item = item;
		string? _title = title;

		public void ClearPayload()
		{
			_item = null;
			_title = null;
		}

		public PayloadSnapshot GetSnapshot()
		{
			var itemBytes = EstimateBytes(_item);
			var titleBytes = EstimateBytes(_title);
			return new PayloadSnapshot(
				HasPayload: itemBytes >= PayloadBytesPerShareText * 0.95,
				EstimatedPayloadBytes: itemBytes + titleBytes);
		}

		public override NSObject GetItemForActivity(UIActivityViewController activityViewController, NSString? activityType) =>
			_item ?? NSString.Empty;

		public override NSObject GetPlaceholderData(UIActivityViewController activityViewController) =>
			_item ?? NSString.Empty;

	}

	internal sealed class RetainedActivityPeer(IntPtr handle)
	{
		public IntPtr Handle { get; } = handle;

		public UIActivityViewController? TryGetPeer()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UIActivityViewController>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record PayloadSnapshot(bool HasPayload, long EstimatedPayloadBytes);

	internal sealed record TrackedCycle(
		WeakReference<object> ActivityControllerWrapper,
		WeakReference<object> ItemSource,
		WeakReference<object> PayloadNSString,
		WeakReference<object> PayloadManagedString)
	{
		public static TrackedCycle Create(
			object activityController,
			object itemSource,
			object payloadNSString,
			object payloadManagedString)
		{
			return new TrackedCycle(
				new WeakReference<object>(activityController),
				new WeakReference<object>(itemSource),
				new WeakReference<object>(payloadNSString),
				new WeakReference<object>(payloadManagedString));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeControllers,
		int AliveActivityControllerWrappers,
		int AliveItemSources,
		int AlivePayloadNSStrings,
		int AlivePayloadManagedStrings,
		int ItemSourcesWithPayload,
		long EstimatedPayloadBytes)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedActivityPeer> retainedControllers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeControllers = 0;
			foreach (var retainedController in retainedControllers)
			{
				if (retainedController.TryGetPeer() is not null)
					retainedNativeControllers++;
			}

			var aliveActivityControllerWrappers = 0;
			var aliveItemSources = 0;
			var alivePayloadNSStrings = 0;
			var alivePayloadManagedStrings = 0;
			var itemSourcesWithPayload = 0;
			long estimatedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.ActivityControllerWrapper.TryGetTarget(out _))
					aliveActivityControllerWrappers++;

				if (cycle.PayloadNSString.TryGetTarget(out _))
					alivePayloadNSStrings++;

				if (cycle.PayloadManagedString.TryGetTarget(out _))
					alivePayloadManagedStrings++;

				if (cycle.ItemSource.TryGetTarget(out var itemSource))
				{
					aliveItemSources++;
					var snapshot = GetPayloadSnapshot(itemSource);
					if (snapshot.HasPayload)
						itemSourcesWithPayload++;

					estimatedPayloadBytes += snapshot.EstimatedPayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativeControllers,
				aliveActivityControllerWrappers,
				aliveItemSources,
				alivePayloadNSStrings,
				alivePayloadManagedStrings,
				itemSourcesWithPayload,
				estimatedPayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerShareText,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeControllers == Cycles &&
		Control.AliveItemSources == Cycles &&
		Control.ItemSourcesWithPayload == 0 &&
		Control.EstimatedPayloadBytes < PayloadKiBPerShareText * 1024L &&
		Current.RetainedNativeControllers == Cycles &&
		Current.AliveItemSources == Cycles &&
		Current.ItemSourcesWithPayload == Cycles &&
		Current.EstimatedPayloadBytes >= Cycles * PayloadKiBPerShareText * 1024L * 0.95 &&
		Current.AliveActivityControllerWrappers <= 1;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosShareSheetItemSourceRetentionRepro",
			$"Cycles per scenario: {Cycles}",
			$"Payload per ShareTextRequest.Text: {PayloadKiBPerShareText} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained share text payload: {controlMiB:N1} MiB",
			$"Current estimated retained share text payload: {currentMiB:N1} MiB",
			"Interpretation:",
			"MAUI Share.ios creates ShareActivityItemSource objects that strongly store the shared item and title.",
			"If native UIActivityViewController peers survive dismissal or delayed native cleanup, the retained item sources keep generated ShareTextRequest.Text payloads alive.",
			"The control keeps the same retained native share-sheet peers and item-source objects, but clears the item-source payload fields after controller creation.",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var retainedMiB = result.EstimatedPayloadBytes / 1024d / 1024d;
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native UIActivityViewControllers: {result.RetainedNativeControllers}/{result.TrackedCycles}",
			$"  alive UIActivityViewController wrappers: {result.AliveActivityControllerWrappers}/{result.TrackedCycles}",
			$"  alive item sources: {result.AliveItemSources}/{result.TrackedCycles}",
			$"  alive payload NSString wrappers: {result.AlivePayloadNSStrings}/{result.TrackedCycles}",
			$"  alive original managed payload strings: {result.AlivePayloadManagedStrings}/{result.TrackedCycles}",
			$"  item sources still carrying payload text: {result.ItemSourcesWithPayload}/{result.TrackedCycles}",
			$"  estimated retained payload bytes: {result.EstimatedPayloadBytes:N0}",
			$"  estimated retained payload MiB: {retainedMiB:N1}");
	}
}
