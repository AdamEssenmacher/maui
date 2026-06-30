#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items;

namespace AndroidCarouselViewTextViewHolderTextRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 80;
	const int ItemsPerCycle = 2;
	const int PayloadCharsPerItem = 256 * 1024;
	const int EstimatedBytesPerChar = 2;
	const string PayloadMarker = "CarouselTextViewHolderPayload:";

	static readonly Type CarouselViewAdapterType = typeof(CarouselViewAdapter<CarouselView, IItemsViewSource>);

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
			"control: explicitly clear retained TextViewHolder native text slots",
			explicitTextClear: true);

		var current = await RunScenarioAsync(
			androidContext,
			"current: CarouselViewAdapter recycle leaves TextViewHolder.TextView.Text assigned",
			explicitTextClear: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedHolderRoots);

		return new ReproReport(
			Cycles,
			ItemsPerCycle,
			PayloadCharsPerItem,
			PayloadCharsPerItem * EstimatedBytesPerChar,
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
		var firstItem = new PayloadItem(cycle, 0, CreatePayloadText(cycle, 0));
		var secondItem = new PayloadItem(cycle, 1, CreatePayloadText(cycle, 1));
		var items = new List<PayloadItem> { firstItem, secondItem };
		var carouselView = new CarouselView
		{
			ItemsSource = items,
			Loop = true
		};

		var adapter = CreateCarouselViewAdapter(carouselView);
		using var parent = new FrameLayout(androidContext);

		var firstHolder = adapter.OnCreateViewHolder(parent, adapter.GetItemViewType(0));
		var secondHolder = adapter.OnCreateViewHolder(parent, adapter.GetItemViewType(1));

		adapter.OnBindViewHolder(firstHolder, 0);
		adapter.OnBindViewHolder(secondHolder, 1);

		ValidateHolder(firstHolder, firstItem.Text);
		ValidateHolder(secondHolder, secondItem.Text);

		var firstTextView = (TextView)firstHolder.ItemView;
		var secondTextView = (TextView)secondHolder.ItemView;

		tracked.Add(TrackedCycle.Create(
			cycle,
			carouselView,
			firstItem,
			secondItem,
			firstItem.Text,
			secondItem.Text,
			firstHolder,
			secondHolder,
			firstTextView,
			secondTextView));

		items.Clear();
		carouselView.ItemsSource = null;
		adapter.OnViewRecycled(firstHolder);
		adapter.OnViewRecycled(secondHolder);
		adapter.Dispose();

		if (explicitTextClear)
		{
			ClearNativeText(firstHolder);
			ClearNativeText(secondHolder);
		}

		RetainedHolderRoots.Add(firstHolder);
		RetainedHolderRoots.Add(secondHolder);

		firstItem = null!;
		secondItem = null!;
		items = null!;
		carouselView = null!;
		adapter = null!;
		firstHolder = null!;
		secondHolder = null!;
		firstTextView = null!;
		secondTextView = null!;
	}

	static string CreatePayloadText(int cycle, int item)
	{
		var prefix = $"{PayloadMarker}{cycle:D4}:{item:D2}:";
		return prefix + new string((char)('A' + (cycle + item) % 26), PayloadCharsPerItem - prefix.Length);
	}

	static void ValidateHolder(RecyclerView.ViewHolder holder, string expectedText)
	{
		if (holder.ItemView is not TextView textView)
			throw new InvalidOperationException($"Expected retained holder ItemView to be TextView, got {holder.ItemView.GetType().FullName}.");

		if (!string.Equals(textView.Text, expectedText, StringComparison.Ordinal))
			throw new InvalidOperationException("TextViewHolder did not receive the expected item text payload.");
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
			text.Length == PayloadCharsPerItem &&
			text.StartsWith(PayloadMarker, StringComparison.Ordinal);
	}

	static CarouselViewAdapter<CarouselView, IItemsViewSource> CreateCarouselViewAdapter(CarouselView carouselView)
	{
		return (CarouselViewAdapter<CarouselView, IItemsViewSource>)Activator.CreateInstance(
			CarouselViewAdapterType,
			BindingFlags.Instance | BindingFlags.NonPublic,
			binder: null,
			args: new object?[] { carouselView, null },
			culture: null)!;
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

	internal sealed class PayloadItem
	{
		public PayloadItem(int cycle, int index, string text)
		{
			Cycle = cycle;
			Index = index;
			Text = text;
		}

		public int Cycle { get; }

		public int Index { get; }

		public string Text { get; }

		public override string ToString() => Text;
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<CarouselView> CarouselView,
		WeakReference<PayloadItem> FirstItem,
		WeakReference<PayloadItem> SecondItem,
		WeakReference<string> FirstItemText,
		WeakReference<string> SecondItemText,
		WeakReference<RecyclerView.ViewHolder> FirstHolder,
		WeakReference<RecyclerView.ViewHolder> SecondHolder,
		WeakReference<TextView> FirstTextView,
		WeakReference<TextView> SecondTextView)
	{
		public static TrackedCycle Create(
			int cycle,
			CarouselView carouselView,
			PayloadItem firstItem,
			PayloadItem secondItem,
			string firstItemText,
			string secondItemText,
			RecyclerView.ViewHolder firstHolder,
			RecyclerView.ViewHolder secondHolder,
			TextView firstTextView,
			TextView secondTextView)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<CarouselView>(carouselView),
				new WeakReference<PayloadItem>(firstItem),
				new WeakReference<PayloadItem>(secondItem),
				new WeakReference<string>(firstItemText),
				new WeakReference<string>(secondItemText),
				new WeakReference<RecyclerView.ViewHolder>(firstHolder),
				new WeakReference<RecyclerView.ViewHolder>(secondHolder),
				new WeakReference<TextView>(firstTextView),
				new WeakReference<TextView>(secondTextView));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveCarouselViews,
		int AliveItemObjects,
		int AliveItemTextStrings,
		int AliveHolderRoots,
		int AliveTextViews,
		int TextViewsWithPayload,
		long RetainedNativeTextBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveCarouselViews = 0;
			var aliveItemObjects = 0;
			var aliveItemTextStrings = 0;
			var aliveHolderRoots = 0;
			var aliveTextViews = 0;
			var textViewsWithPayload = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.CarouselView.TryGetTarget(out _))
					aliveCarouselViews++;

				if (cycle.FirstItem.TryGetTarget(out _))
					aliveItemObjects++;

				if (cycle.SecondItem.TryGetTarget(out _))
					aliveItemObjects++;

				if (cycle.FirstItemText.TryGetTarget(out _))
					aliveItemTextStrings++;

				if (cycle.SecondItemText.TryGetTarget(out _))
					aliveItemTextStrings++;

				if (cycle.FirstHolder.TryGetTarget(out _))
					aliveHolderRoots++;

				if (cycle.SecondHolder.TryGetTarget(out _))
					aliveHolderRoots++;

				if (cycle.FirstTextView.TryGetTarget(out var firstTextView))
				{
					aliveTextViews++;
					if (HasPayloadText(firstTextView))
						textViewsWithPayload++;
				}

				if (cycle.SecondTextView.TryGetTarget(out var secondTextView))
				{
					aliveTextViews++;
					if (HasPayloadText(secondTextView))
						textViewsWithPayload++;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveCarouselViews,
				aliveItemObjects,
				aliveItemTextStrings,
				aliveHolderRoots,
				aliveTextViews,
				textViewsWithPayload,
				(long)textViewsWithPayload * PayloadCharsPerItem * EstimatedBytesPerChar);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int ItemsPerCycle,
	int PayloadCharsPerItem,
	int EstimatedNativeBytesPerItem,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int HolderCount => Cycles * ItemsPerCycle;

	public bool LeakProved =>
		Control.AliveCarouselViews == 0 &&
		Control.AliveItemObjects == 0 &&
		Control.AliveItemTextStrings == 0 &&
		Control.AliveHolderRoots == HolderCount &&
		Control.AliveTextViews == HolderCount &&
		Control.TextViewsWithPayload == 0 &&
		Control.RetainedNativeTextBytes == 0 &&
		Current.AliveCarouselViews == 0 &&
		Current.AliveItemObjects == 0 &&
		Current.AliveItemTextStrings == 0 &&
		Current.AliveHolderRoots == HolderCount &&
		Current.AliveTextViews == HolderCount &&
		Current.TextViewsWithPayload == HolderCount &&
		Current.RetainedNativeTextBytes == (long)HolderCount * EstimatedNativeBytesPerItem;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidCarouselViewTextViewHolderTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"No-template item holders per cycle: {ItemsPerCycle}",
			$"Payload chars per item: {PayloadCharsPerItem:N0}",
			$"Estimated native text bytes per item: {EstimatedNativeBytesPerItem:N0}",
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

	static string Format(ReproSession.ScenarioResult result)
	{
		var holderCount = result.TrackedCycles * 2;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  expected holder roots for this run: {holderCount}",
			$"  alive CarouselViews: {result.AliveCarouselViews}/{result.TrackedCycles}",
			$"  alive item objects: {result.AliveItemObjects}/{holderCount}",
			$"  alive item text strings: {result.AliveItemTextStrings}/{holderCount}",
			$"  alive holder roots: {result.AliveHolderRoots}/{holderCount}",
			$"  alive TextViews: {result.AliveTextViews}/{holderCount}",
			$"  TextViews with payload text: {result.TextViewsWithPayload}/{holderCount}",
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
