#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using AView = Android.Views.View;

namespace AndroidListViewCellContentDescriptionRetentionRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveNativeViews,
	int AliveRenderers,
	int AliveCells,
	int AssignedContentDescriptions,
	int PayloadSizedContentDescriptions,
	long RetainedContentDescriptionBytes);

public sealed record ReproReport(
	int Attempts,
	int PayloadChars,
	int PayloadBytesPerSlot,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveNativeViews == Attempts &&
		Current.AliveNativeViews == Attempts &&
		Control.AliveCells <= 1 &&
		Current.AliveCells <= 1 &&
		Control.AliveRenderers <= 1 &&
		Current.AliveRenderers <= 1 &&
		Control.PayloadSizedContentDescriptions == 0 &&
		Current.PayloadSizedContentDescriptions == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidListViewCellContentDescriptionRetentionRepro",
			$"Attempts: {Attempts}",
			$"Payload chars per ContentDescription: {PayloadChars}",
			$"Payload bytes per ContentDescription: {PayloadBytesPerSlot}",
			$"Baseline managed heap: {ManagedHeapBaseline:N0} bytes",
			$"Final managed heap: {ManagedHeapFinal:N0} bytes",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native ContentDescription payload: {FormatBytes(Control.RetainedContentDescriptionBytes)}",
			$"Current retained native ContentDescription payload: {FormatBytes(Current.RetainedContentDescriptionBytes)}",
			LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained native cell views: {stats.Attempts}",
			$"  native views alive after full GC: {stats.AliveNativeViews}/{stats.Attempts}",
			$"  renderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}",
			$"  cells alive after full GC: {stats.AliveCells}/{stats.Attempts}",
			$"  assigned native ContentDescription slots: {stats.AssignedContentDescriptions}/{stats.Attempts}",
			$"  payload-sized native ContentDescription slots: {stats.PayloadSizedContentDescriptions}/{stats.Attempts}",
			$"  retained native ContentDescription bytes: {stats.RetainedContentDescriptionBytes:N0}");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : "";
		var value = Math.Abs((double)bytes);
		if (value >= 1024 * 1024)
			return $"{sign}{value / 1024 / 1024:0.0} MiB";
		if (value >= 1024)
			return $"{sign}{value / 1024:0.0} KiB";
		return $"{bytes} B";
	}
}

internal static class ReproSession
{
	const int Attempts = 1024;
	const int PayloadChars = 16 * 1024;
	const int PayloadBytesPerSlot = PayloadChars * 2;

	static readonly FieldInfo BaseCellViewCellField =
		typeof(BaseCellView).GetField("_cell", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(BaseCellView), "_cell");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: clear native ContentDescription after neutralizing known BaseCellView._cell root",
			clearNativeContentDescription: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: MAUI disconnect leaves native ContentDescription assigned after known BaseCellView._cell root is neutralized",
			clearNativeContentDescription: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadChars, PayloadBytesPerSlot, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(
		IMauiContext mauiContext,
		string name,
		bool clearNativeContentDescription)
	{
		var retainedNativeViews = new List<AView>(Attempts);
		var nativeViewRefs = new List<WeakReference<AView>>(Attempts);
		var rendererRefs = new List<WeakReference<TextCellRenderer>>(Attempts);
		var cellRefs = new List<WeakReference<TextCell>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedTextCell(
				mauiContext,
				clearNativeContentDescription,
				retainedNativeViews,
				nativeViewRefs,
				rendererRefs,
				cellRefs,
				i);

			if (i % 64 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedNativeViews);

		var aliveNativeViews = nativeViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveRenderers = rendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveCells = cellRefs.Count(static wr => wr.TryGetTarget(out _));
		var contentDescriptionLengths = retainedNativeViews.Select(GetContentDescriptionLength).ToArray();
		var assignedContentDescriptions = contentDescriptionLengths.Count(static length => length > 0);
		var payloadSizedContentDescriptions = contentDescriptionLengths.Count(static length => length >= PayloadChars);
		var retainedContentDescriptionBytes = contentDescriptionLengths.Sum(static length => (long)length * 2);

		return new RunStats(
			name,
			Attempts,
			aliveNativeViews,
			aliveRenderers,
			aliveCells,
			assignedContentDescriptions,
			payloadSizedContentDescriptions,
			retainedContentDescriptionBytes);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisconnectedTextCell(
		IMauiContext mauiContext,
		bool clearNativeContentDescription,
		List<AView> retainedNativeViews,
		List<WeakReference<AView>> nativeViewRefs,
		List<WeakReference<TextCellRenderer>> rendererRefs,
		List<WeakReference<TextCell>> cellRefs,
		int index)
	{
		var cell = new TextCell
		{
			Text = $"Row {index}",
			Detail = "AutomationId payload should not remain through managed Cell state",
			AutomationId = CreatePayload(index)
		};

		cellRefs.Add(new WeakReference<TextCell>(cell));

		var renderer = new TextCellRenderer
		{
			ParentView = new ContentView { FlowDirection = FlowDirection.LeftToRight }
		};
		((IElementHandler)renderer).SetMauiContext(mauiContext);
		((IElementHandler)renderer).SetVirtualView(cell);
		rendererRefs.Add(new WeakReference<TextCellRenderer>(renderer));

		var nativeView = ((IElementHandler)renderer).PlatformView as AView
			?? throw new InvalidOperationException("Expected Android native cell view.");
		retainedNativeViews.Add(nativeView);
		nativeViewRefs.Add(new WeakReference<AView>(nativeView));

		if (nativeView is BaseCellView baseCellView)
			BaseCellViewCellField.SetValue(baseCellView, null);

		if (clearNativeContentDescription)
			nativeView.ContentDescription = null;

		((IElementHandler)renderer).DisconnectHandler();
	}

	static string CreatePayload(int index)
	{
		var prefix = $"android-listview-cell-contentdescription-{index:D4}-";
		return prefix + new string((char)('A' + (index % 26)), PayloadChars - prefix.Length);
	}

	static int GetContentDescriptionLength(AView view)
	{
		var contentDescription = view.ContentDescription;
		return contentDescription?.Length ?? 0;
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
