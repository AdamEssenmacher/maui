#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Foundation;
using Microsoft.Maui.Controls;

namespace MultiPageItemsSourceResetRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	const int ItemCount = 80;
	const int PayloadBytes = 1024 * 1024;

	readonly string? _resultsPath;
	readonly Label _status;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		_status = new Label
		{
			Text = "Running MultiPage ItemsSource reset retention leak repro...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		string text;
		try
		{
			var result = await RunScenariosAsync();
			text = result.ToText();
		}
		catch (Exception ex)
		{
			text = "RESULT: FAILED" + Environment.NewLine + ex;
		}

		_status.Text = text;

		if (!string.IsNullOrWhiteSpace(_resultsPath))
			System.IO.File.WriteAllText(_resultsPath, text);

		await Task.Delay(250);
		Process.GetCurrentProcess().Kill();
	}

	static async Task<ReproResult> RunScenariosAsync()
	{
		var control = await RunScenarioAsync("control: remove generated pages one at a time", clearWithReset: false);
		var current = await RunScenarioAsync("current: ItemsSource.Clear reset leaves logical children", clearWithReset: true);

		return new ReproResult(ItemCount, PayloadBytes, control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool clearWithReset)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var retainedTabbedPages = new List<TabbedPage>(1);
		var pageReferences = new List<WeakReference<ContentPage>>(ItemCount);
		var pagePayloadReferences = new List<WeakReference<PagePayload>>(ItemCount);
		var pagePayloadBufferReferences = new List<WeakReference<byte[]>>(ItemCount);
		var payloadReferences = new List<WeakReference<PayloadItem>>(ItemCount);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(ItemCount);

		int childrenAfterRemoval;
		int logicalChildrenAfterRemoval;

		using (new NSAutoreleasePool())
		{
			PayloadContentPage.ResetPageIndex();
			var source = new ObservableCollection<PayloadItem>();

			for (var i = 0; i < ItemCount; i++)
			{
				var item = new PayloadItem(i, PayloadBytes);
				source.Add(item);
				payloadReferences.Add(new WeakReference<PayloadItem>(item));
				payloadBufferReferences.Add(new WeakReference<byte[]>(item.Buffer));
			}

			var tabbedPage = new TabbedPage
			{
				ItemTemplate = new DataTemplate(() => new PayloadContentPage(PayloadBytes))
			};

			tabbedPage.ItemsSource = source;

			if (tabbedPage.Children.Count != ItemCount)
				throw new InvalidOperationException($"Expected {ItemCount} generated pages, got {tabbedPage.Children.Count}.");

			foreach (var page in tabbedPage.Children.Cast<PayloadContentPage>())
			{
				if (page.BindingContext is not PayloadItem)
					throw new InvalidOperationException("Generated page did not receive the payload item BindingContext.");

				pageReferences.Add(new WeakReference<ContentPage>(page));
				pagePayloadReferences.Add(new WeakReference<PagePayload>(page.Payload));
				pagePayloadBufferReferences.Add(new WeakReference<byte[]>(page.Payload.Buffer));
			}

			if (clearWithReset)
			{
				source.Clear();
			}
			else
			{
				while (source.Count > 0)
					source.RemoveAt(source.Count - 1);
			}

			childrenAfterRemoval = tabbedPage.Children.Count;
#pragma warning disable CS0618
			logicalChildrenAfterRemoval = tabbedPage.LogicalChildren.Count;
#pragma warning restore CS0618

			retainedTabbedPages.Add(tabbedPage);
			source = null!;
			tabbedPage = null!;
		}

		await Task.Delay(250);
		ForceGc();
		await Task.Delay(250);
		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			name,
			CountAlive(pageReferences),
			CountAlive(pagePayloadReferences),
			CountAlive(pagePayloadBufferReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			childrenAfterRemoval,
			logicalChildrenAfterRemoval,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedTabbedPages);
		return result;
	}

	static int CountAlive<T>(IEnumerable<WeakReference<T>> references)
		where T : class
	{
		var count = 0;

		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out _))
				count++;
		}

		return count;
	}

	static void ForceGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}
}

public sealed record ReproResult(
	int ItemCount,
	int PayloadBytes,
	ScenarioResult Control,
	ScenarioResult Current)
{
	public bool LeakProved =>
		Control.ChildrenAfterRemoval == 0 &&
		Control.LogicalChildrenAfterRemoval == 0 &&
		Control.AlivePages == 0 &&
		Control.AlivePagePayloads == 0 &&
		Control.AlivePagePayloadBuffers == 0 &&
		Current.ChildrenAfterRemoval == 0 &&
		Current.LogicalChildrenAfterRemoval == ItemCount &&
		Current.AlivePages == ItemCount &&
		Current.AlivePagePayloads == ItemCount &&
		Current.AlivePagePayloadBuffers == ItemCount;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"MultiPageItemsSourceResetRetentionLeakRepro",
			$"Items per run: {ItemCount}",
			$"Payload per item: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Control.ToText(ItemCount, PayloadBytes),
			string.Empty,
			Current.ToText(ItemCount, PayloadBytes));
	}
}

public sealed record ScenarioResult(
	string Name,
	int AlivePages,
	int AlivePagePayloads,
	int AlivePagePayloadBuffers,
	int AlivePayloads,
	int AlivePayloadBuffers,
	int ChildrenAfterRemoval,
	int LogicalChildrenAfterRemoval,
	long HeapBefore,
	long HeapAfter)
{
	public string ToText(int itemCount, int payloadBytes)
	{
		var retainedPayloadBytes = (long)AlivePagePayloadBuffers * payloadBytes;

		return string.Join(Environment.NewLine,
			$"Run: {Name}",
			$"  TabbedPage.Children count after removal: {ChildrenAfterRemoval}",
			$"  TabbedPage logical children after removal: {LogicalChildrenAfterRemoval}",
			$"  generated pages alive after full GC: {AlivePages}/{itemCount}",
			$"  generated page payloads alive after full GC: {AlivePagePayloads}/{itemCount}",
			$"  generated page payload byte arrays alive after full GC: {AlivePagePayloadBuffers}/{itemCount}",
			$"  item BindingContext payloads alive after full GC: {AlivePayloads}/{itemCount}",
			$"  item BindingContext payload byte arrays alive after full GC: {AlivePayloadBuffers}/{itemCount}",
			$"  retained payload bytes: {FormatBytes(retainedPayloadBytes)} ({retainedPayloadBytes * 100.0 / (payloadBytes * itemCount):0.0}%)",
			$"  managed heap before: {FormatBytes(HeapBefore)}",
			$"  managed heap after: {FormatBytes(HeapAfter)}",
			$"  managed heap delta: {FormatBytes(HeapAfter - HeapBefore)}");
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

public sealed class PayloadContentPage : ContentPage
{
	static int s_pageIndex;

	public PayloadContentPage(int payloadSize)
	{
		var index = s_pageIndex++;
		Payload = new PagePayload(index, payloadSize);
		Title = "Generated page " + index;
		Content = new Label
		{
			Text = "Generated page " + index
		};
	}

	public PagePayload Payload { get; }

	public static void ResetPageIndex()
	{
		s_pageIndex = 0;
	}
}

public sealed class PagePayload
{
	public PagePayload(int index, int size)
	{
		Buffer = new byte[size];

		for (var i = 0; i < Buffer.Length; i += 4096)
			Buffer[i] = (byte)(index + i);
	}

	public byte[] Buffer { get; }
}

public sealed class PayloadItem
{
	public PayloadItem(int index, int size)
	{
		Buffer = new byte[size];

		for (var i = 0; i < Buffer.Length; i += 4096)
			Buffer[i] = (byte)(index + i);
	}

	public byte[] Buffer { get; }
}
