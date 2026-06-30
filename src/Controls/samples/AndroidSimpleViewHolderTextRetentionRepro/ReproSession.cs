#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items;

namespace AndroidSimpleViewHolderTextRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 80;
	const int HoldersPerCycle = 4;
	const int CollectionViewsPerCycle = 2;
	const int PayloadCharsPerText = 128 * 1024;
	const int EstimatedBytesPerChar = 2;
	const string PayloadMarker = "SimpleViewHolderTextPayload:";

	static readonly List<RecyclerView.ViewHolder> RetainedHolderRoots = new();

	public static async Task<ReproReport> RunAsync(IMauiContext rootContext)
	{
		RetainedHolderRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var androidContext = rootContext.Context
			?? Android.App.Application.Context
			?? throw new InvalidOperationException("No Android context is available.");

		var control = await RunScenarioAsync(
			androidContext,
			"control: explicitly clear retained SimpleViewHolder native text slots",
			explicitTextClear: true);

		var current = await RunScenarioAsync(
			androidContext,
			"current: header/footer SimpleViewHolder recycle leaves TextView.Text assigned",
			explicitTextClear: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedHolderRoots);

		return new ReproReport(
			Cycles,
			HoldersPerCycle,
			CollectionViewsPerCycle,
			PayloadCharsPerText,
			PayloadCharsPerText * EstimatedBytesPerChar,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		Context androidContext,
		string name,
		bool explicitTextClear)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(androidContext, i, tracked, explicitTextClear);

			if (i % 16 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateCycle(
		Context androidContext,
		int cycle,
		List<TrackedCycle> tracked,
		bool explicitTextClear)
	{
		var structuredHeader = new PayloadText(cycle, 0, CreatePayloadText(cycle, 0));
		var structuredFooter = new PayloadText(cycle, 1, CreatePayloadText(cycle, 1));
		var emptyHeader = new PayloadText(cycle, 2, CreatePayloadText(cycle, 2));
		var emptyFooter = new PayloadText(cycle, 3, CreatePayloadText(cycle, 3));
		var emptyItems = Array.Empty<object>();

		var structuredView = new CollectionView
		{
			ItemsSource = emptyItems,
			Header = structuredHeader,
			Footer = structuredFooter
		};

		var emptyView = new CollectionView
		{
			Header = emptyHeader,
			Footer = emptyFooter,
			EmptyView = "No items"
		};

		var structuredAdapter = new ProbeStructuredItemsViewAdapter(structuredView);
		var emptyAdapter = new ProbeEmptyViewAdapter(emptyView)
		{
			Header = emptyHeader,
			Footer = emptyFooter,
			EmptyView = emptyView.EmptyView
		};

		using var parent = new FrameLayout(androidContext);

		var structuredHeaderHolder = structuredAdapter.OnCreateViewHolder(parent, structuredAdapter.GetItemViewType(0));
		var structuredFooterHolder = structuredAdapter.OnCreateViewHolder(parent, structuredAdapter.GetItemViewType(structuredAdapter.ItemCount - 1));
		var emptyHeaderHolder = emptyAdapter.OnCreateViewHolder(parent, emptyAdapter.GetItemViewType(0));
		var emptyFooterHolder = emptyAdapter.OnCreateViewHolder(parent, emptyAdapter.GetItemViewType(emptyAdapter.ItemCount - 1));

		structuredAdapter.OnBindViewHolder(structuredHeaderHolder, 0);
		structuredAdapter.OnBindViewHolder(structuredFooterHolder, structuredAdapter.ItemCount - 1);
		emptyAdapter.OnBindViewHolder(emptyHeaderHolder, 0);
		emptyAdapter.OnBindViewHolder(emptyFooterHolder, emptyAdapter.ItemCount - 1);

		ValidateHolder(structuredHeaderHolder, structuredHeader.Text);
		ValidateHolder(structuredFooterHolder, structuredFooter.Text);
		ValidateHolder(emptyHeaderHolder, emptyHeader.Text);
		ValidateHolder(emptyFooterHolder, emptyFooter.Text);

		var structuredHeaderTextView = (TextView)structuredHeaderHolder.ItemView;
		var structuredFooterTextView = (TextView)structuredFooterHolder.ItemView;
		var emptyHeaderTextView = (TextView)emptyHeaderHolder.ItemView;
		var emptyFooterTextView = (TextView)emptyFooterHolder.ItemView;

		tracked.Add(TrackedCycle.Create(
			cycle,
			structuredView,
			emptyView,
			structuredHeader,
			structuredFooter,
			emptyHeader,
			emptyFooter,
			structuredHeader.Text,
			structuredFooter.Text,
			emptyHeader.Text,
			emptyFooter.Text,
			structuredHeaderHolder,
			structuredFooterHolder,
			emptyHeaderHolder,
			emptyFooterHolder,
			structuredHeaderTextView,
			structuredFooterTextView,
			emptyHeaderTextView,
			emptyFooterTextView));

		structuredView.Header = null;
		structuredView.Footer = null;
		structuredView.ItemsSource = null;
		emptyView.Header = null;
		emptyView.Footer = null;
		emptyView.EmptyView = null;
		emptyAdapter.Header = null;
		emptyAdapter.Footer = null;
		emptyAdapter.EmptyView = null;

		structuredAdapter.OnViewRecycled(structuredHeaderHolder);
		structuredAdapter.OnViewRecycled(structuredFooterHolder);
		emptyAdapter.OnViewRecycled(emptyHeaderHolder);
		emptyAdapter.OnViewRecycled(emptyFooterHolder);
		structuredAdapter.Dispose();
		emptyAdapter.Dispose();

		if (explicitTextClear)
		{
			ClearNativeText(structuredHeaderHolder);
			ClearNativeText(structuredFooterHolder);
			ClearNativeText(emptyHeaderHolder);
			ClearNativeText(emptyFooterHolder);
		}

		RetainedHolderRoots.Add(structuredHeaderHolder);
		RetainedHolderRoots.Add(structuredFooterHolder);
		RetainedHolderRoots.Add(emptyHeaderHolder);
		RetainedHolderRoots.Add(emptyFooterHolder);

		structuredHeader = null!;
		structuredFooter = null!;
		emptyHeader = null!;
		emptyFooter = null!;
		emptyItems = null!;
		structuredView = null!;
		emptyView = null!;
		structuredAdapter = null!;
		emptyAdapter = null!;
		structuredHeaderHolder = null!;
		structuredFooterHolder = null!;
		emptyHeaderHolder = null!;
		emptyFooterHolder = null!;
		structuredHeaderTextView = null!;
		structuredFooterTextView = null!;
		emptyHeaderTextView = null!;
		emptyFooterTextView = null!;
	}

	static string CreatePayloadText(int cycle, int slot)
	{
		var prefix = $"{PayloadMarker}{cycle:D4}:{slot:D2}:";
		return prefix + new string((char)('A' + (cycle + slot) % 26), PayloadCharsPerText - prefix.Length);
	}

	static void ValidateHolder(RecyclerView.ViewHolder holder, string expectedText)
	{
		if (holder.ItemView is not TextView textView)
			throw new InvalidOperationException($"Expected retained holder ItemView to be TextView, got {holder.ItemView.GetType().FullName}.");

		if (!string.Equals(textView.Text, expectedText, StringComparison.Ordinal))
			throw new InvalidOperationException("SimpleViewHolder did not receive the expected header/footer text payload.");
	}

	static void ClearNativeText(RecyclerView.ViewHolder holder)
	{
		if (holder.ItemView is TextView textView)
			textView.Text = string.Empty;
	}

	static bool HasPayloadText(TextView textView)
	{
		var text = textView.Text;
		return text is not null &&
			text.Length == PayloadCharsPerText &&
			text.StartsWith(PayloadMarker, StringComparison.Ordinal);
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	sealed class ProbeStructuredItemsViewAdapter : StructuredItemsViewAdapter<CollectionView, IItemsViewSource>
	{
		public ProbeStructuredItemsViewAdapter(CollectionView itemsView)
			: base(itemsView)
		{
		}
	}

	sealed class ProbeEmptyViewAdapter : EmptyViewAdapter
	{
		public ProbeEmptyViewAdapter(ItemsView itemsView)
			: base(itemsView)
		{
		}
	}

	internal sealed class PayloadText
	{
		public PayloadText(int cycle, int slot, string text)
		{
			Cycle = cycle;
			Slot = slot;
			Text = text;
		}

		public int Cycle { get; }

		public int Slot { get; }

		public string Text { get; }

		public override string ToString() => Text;
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<CollectionView> StructuredView,
		WeakReference<CollectionView> EmptyView,
		WeakReference<PayloadText> StructuredHeader,
		WeakReference<PayloadText> StructuredFooter,
		WeakReference<PayloadText> EmptyHeader,
		WeakReference<PayloadText> EmptyFooter,
		WeakReference<string> StructuredHeaderText,
		WeakReference<string> StructuredFooterText,
		WeakReference<string> EmptyHeaderText,
		WeakReference<string> EmptyFooterText,
		WeakReference<RecyclerView.ViewHolder> StructuredHeaderHolder,
		WeakReference<RecyclerView.ViewHolder> StructuredFooterHolder,
		WeakReference<RecyclerView.ViewHolder> EmptyHeaderHolder,
		WeakReference<RecyclerView.ViewHolder> EmptyFooterHolder,
		WeakReference<TextView> StructuredHeaderTextView,
		WeakReference<TextView> StructuredFooterTextView,
		WeakReference<TextView> EmptyHeaderTextView,
		WeakReference<TextView> EmptyFooterTextView)
	{
		public static TrackedCycle Create(
			int cycle,
			CollectionView structuredView,
			CollectionView emptyView,
			PayloadText structuredHeader,
			PayloadText structuredFooter,
			PayloadText emptyHeader,
			PayloadText emptyFooter,
			string structuredHeaderText,
			string structuredFooterText,
			string emptyHeaderText,
			string emptyFooterText,
			RecyclerView.ViewHolder structuredHeaderHolder,
			RecyclerView.ViewHolder structuredFooterHolder,
			RecyclerView.ViewHolder emptyHeaderHolder,
			RecyclerView.ViewHolder emptyFooterHolder,
			TextView structuredHeaderTextView,
			TextView structuredFooterTextView,
			TextView emptyHeaderTextView,
			TextView emptyFooterTextView)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<CollectionView>(structuredView),
				new WeakReference<CollectionView>(emptyView),
				new WeakReference<PayloadText>(structuredHeader),
				new WeakReference<PayloadText>(structuredFooter),
				new WeakReference<PayloadText>(emptyHeader),
				new WeakReference<PayloadText>(emptyFooter),
				new WeakReference<string>(structuredHeaderText),
				new WeakReference<string>(structuredFooterText),
				new WeakReference<string>(emptyHeaderText),
				new WeakReference<string>(emptyFooterText),
				new WeakReference<RecyclerView.ViewHolder>(structuredHeaderHolder),
				new WeakReference<RecyclerView.ViewHolder>(structuredFooterHolder),
				new WeakReference<RecyclerView.ViewHolder>(emptyHeaderHolder),
				new WeakReference<RecyclerView.ViewHolder>(emptyFooterHolder),
				new WeakReference<TextView>(structuredHeaderTextView),
				new WeakReference<TextView>(structuredFooterTextView),
				new WeakReference<TextView>(emptyHeaderTextView),
				new WeakReference<TextView>(emptyFooterTextView));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveCollectionViews,
		int AlivePayloadObjects,
		int AlivePayloadTextStrings,
		int AliveHolderRoots,
		int AliveTextViews,
		int TextViewsWithPayload,
		long RetainedNativeTextBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveCollectionViews = 0;
			var alivePayloadObjects = 0;
			var alivePayloadTextStrings = 0;
			var aliveHolderRoots = 0;
			var aliveTextViews = 0;
			var textViewsWithPayload = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.StructuredView.TryGetTarget(out _))
					aliveCollectionViews++;

				if (cycle.EmptyView.TryGetTarget(out _))
					aliveCollectionViews++;

				if (cycle.StructuredHeader.TryGetTarget(out _))
					alivePayloadObjects++;

				if (cycle.StructuredFooter.TryGetTarget(out _))
					alivePayloadObjects++;

				if (cycle.EmptyHeader.TryGetTarget(out _))
					alivePayloadObjects++;

				if (cycle.EmptyFooter.TryGetTarget(out _))
					alivePayloadObjects++;

				if (cycle.StructuredHeaderText.TryGetTarget(out _))
					alivePayloadTextStrings++;

				if (cycle.StructuredFooterText.TryGetTarget(out _))
					alivePayloadTextStrings++;

				if (cycle.EmptyHeaderText.TryGetTarget(out _))
					alivePayloadTextStrings++;

				if (cycle.EmptyFooterText.TryGetTarget(out _))
					alivePayloadTextStrings++;

				if (cycle.StructuredHeaderHolder.TryGetTarget(out _))
					aliveHolderRoots++;

				if (cycle.StructuredFooterHolder.TryGetTarget(out _))
					aliveHolderRoots++;

				if (cycle.EmptyHeaderHolder.TryGetTarget(out _))
					aliveHolderRoots++;

				if (cycle.EmptyFooterHolder.TryGetTarget(out _))
					aliveHolderRoots++;

				if (cycle.StructuredHeaderTextView.TryGetTarget(out var structuredHeaderTextView))
				{
					aliveTextViews++;
					if (HasPayloadText(structuredHeaderTextView))
						textViewsWithPayload++;
				}

				if (cycle.StructuredFooterTextView.TryGetTarget(out var structuredFooterTextView))
				{
					aliveTextViews++;
					if (HasPayloadText(structuredFooterTextView))
						textViewsWithPayload++;
				}

				if (cycle.EmptyHeaderTextView.TryGetTarget(out var emptyHeaderTextView))
				{
					aliveTextViews++;
					if (HasPayloadText(emptyHeaderTextView))
						textViewsWithPayload++;
				}

				if (cycle.EmptyFooterTextView.TryGetTarget(out var emptyFooterTextView))
				{
					aliveTextViews++;
					if (HasPayloadText(emptyFooterTextView))
						textViewsWithPayload++;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveCollectionViews,
				alivePayloadObjects,
				alivePayloadTextStrings,
				aliveHolderRoots,
				aliveTextViews,
				textViewsWithPayload,
				(long)textViewsWithPayload * PayloadCharsPerText * EstimatedBytesPerChar);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int HoldersPerCycle,
	int CollectionViewsPerCycle,
	int PayloadCharsPerText,
	int EstimatedNativeBytesPerText,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int HolderCount => Cycles * HoldersPerCycle;
	int CollectionViewCount => Cycles * CollectionViewsPerCycle;

	public bool LeakProved =>
		Control.AliveCollectionViews == 0 &&
		Control.AlivePayloadObjects == 0 &&
		Control.AlivePayloadTextStrings == 0 &&
		Control.AliveHolderRoots == HolderCount &&
		Control.AliveTextViews == HolderCount &&
		Control.TextViewsWithPayload == 0 &&
		Control.RetainedNativeTextBytes == 0 &&
		Current.AliveCollectionViews == 0 &&
		Current.AlivePayloadObjects == 0 &&
		Current.AlivePayloadTextStrings == 0 &&
		Current.AliveHolderRoots == HolderCount &&
		Current.AliveTextViews == HolderCount &&
		Current.TextViewsWithPayload == HolderCount &&
		Current.RetainedNativeTextBytes == (long)HolderCount * EstimatedNativeBytesPerText;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidSimpleViewHolderTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"No-template header/footer text holders per cycle: {HoldersPerCycle}",
			$"CollectionViews per cycle: {CollectionViewsPerCycle}",
			$"Payload chars per text slot: {PayloadCharsPerText:N0}",
			$"Estimated native text bytes per slot: {EstimatedNativeBytesPerText:N0}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native text: {FormatBytes(Control.RetainedNativeTextBytes)}",
			$"Current retained native text: {FormatBytes(Current.RetainedNativeTextBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  expected CollectionViews for this run: {CollectionViewCount}",
			$"  expected holder roots for this run: {HolderCount}",
			$"  alive CollectionViews: {result.AliveCollectionViews}/{CollectionViewCount}",
			$"  alive payload objects: {result.AlivePayloadObjects}/{HolderCount}",
			$"  alive payload text strings: {result.AlivePayloadTextStrings}/{HolderCount}",
			$"  alive holder roots: {result.AliveHolderRoots}/{HolderCount}",
			$"  alive TextViews: {result.AliveTextViews}/{HolderCount}",
			$"  TextViews with payload text: {result.TextViewsWithPayload}/{HolderCount}",
			$"  retained native text bytes: {result.RetainedNativeTextBytes:N0}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes == 0)
			return "0 B";

		var mib = bytes / 1024d / 1024d;
		return $"{mib:N1} MiB";
	}
}
