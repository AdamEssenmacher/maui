using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;

namespace ListViewTemplatedItemsRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new RunnerPage());
	}
}

sealed class RunnerPage : ContentPage
{
	bool _ran;

	public RunnerPage()
	{
		Content = new Label
		{
			Text = "Running ListView.TemplatedItems retention repro...",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await TryRunAsync();
	}

	protected override async void OnHandlerChanged()
	{
		base.OnHandlerChanged();
		await TryRunAsync();
	}

	async Task TryRunAsync()
	{
		if (_ran || Handler?.MauiContext is null)
			return;

		_ran = true;
		await Task.Delay(250);

		try
		{
			var report = ReproSession.Run();
			var text = report.ToText();
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(report.LeakProved ? 0 : 2);
		}
		catch (Exception ex)
		{
			var text = "ListViewTemplatedItemsRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/listview-templateditems-retention-results.txt";

	const int Iterations = 160;
	const int ItemsPerListView = 3;
	const int PayloadBytes = 1024 * 1024;

	public static ReproReport Run()
	{
		var control = RunScenario(clearOwnerReference: true);
		var current = RunScenario(clearOwnerReference: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearOwnerReference)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedTemplatedItems = new List<object>(Iterations);
		var ownerReferences = new List<WeakReference<ListView>>(Iterations);
		var payloadReferences = new List<WeakReference<ListViewPayload>>(Iterations);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations);

		for (var i = 0; i < Iterations; i++)
			CreateRetainedTemplatedItems(i, clearOwnerReference, retainedTemplatedItems, ownerReferences, payloadReferences, payloadBufferReferences);

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(ownerReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			retainedTemplatedItems.Count,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedTemplatedItems);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRetainedTemplatedItems(
		int iteration,
		bool clearOwnerReference,
		List<object> retainedTemplatedItems,
		List<WeakReference<ListView>> ownerReferences,
		List<WeakReference<ListViewPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new ListViewPayload($"orders-list-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)iteration;

		var rows = Enumerable.Range(0, ItemsPerListView)
			.Select(index => new OrderRow($"SO-{iteration:000}-{index:00}", $"Customer {iteration}", 100 + index))
			.ToList();

		var owner = new ListView
		{
			AutomationId = $"orders-list-{iteration}",
			BindingContext = payload,
			ItemsSource = rows,
			ItemTemplate = new DataTemplate(() => new TextCell())
		};

		var templatedItems = owner.TemplatedItems;
		_ = templatedItems.Count;

		if (clearOwnerReference)
			ClearItemsViewReference(templatedItems);

		retainedTemplatedItems.Add(templatedItems);
		ownerReferences.Add(new WeakReference<ListView>(owner));
		payloadReferences.Add(new WeakReference<ListViewPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		owner = null!;
		payload = null!;
		rows = null!;
		templatedItems = null!;
	}

	static void ClearItemsViewReference(object templatedItems)
	{
		var type = templatedItems.GetType();
		while (type is not null)
		{
			var field = type.GetField("_itemsView", BindingFlags.Instance | BindingFlags.NonPublic);
			if (field is not null)
			{
				field.SetValue(templatedItems, null);
				return;
			}

			type = type.BaseType;
		}

		throw new InvalidOperationException("Could not find TemplatedItemsList._itemsView.");
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
		for (var i = 0; i < 6; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(75);
		}
	}

	sealed class ListViewPayload
	{
		public ListViewPayload(string name, byte[] buffer)
		{
			Name = name;
			Buffer = buffer;
		}

		public string Name { get; }

		public byte[] Buffer { get; }
	}

	sealed class OrderRow
	{
		public OrderRow(string orderNumber, string customerName, decimal total)
		{
			OrderNumber = orderNumber;
			CustomerName = customerName;
			Total = total;
		}

		public string OrderNumber { get; }

		public string CustomerName { get; }

		public decimal Total { get; }
	}

	public readonly record struct ScenarioResult(
		int OwnersAlive,
		int PayloadsAlive,
		int PayloadBuffersAlive,
		int RetainedTemplatedItems,
		long HeapBefore,
		long HeapAfter)
	{
		public long HeapDelta => HeapAfter - HeapBefore;
	}

	public readonly record struct ReproReport(ScenarioResult Control, ScenarioResult Current)
	{
		public bool LeakProved =>
			Control.OwnersAlive == 0 &&
			Control.PayloadsAlive == 0 &&
			Control.PayloadBuffersAlive == 0 &&
			Current.OwnersAlive == Iterations &&
			Current.PayloadsAlive == Iterations &&
			Current.PayloadBuffersAlive == Iterations;

		public string ToText()
		{
			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("ListViewTemplatedItemsRetentionRepro");
			builder.AppendLine($"ListView owners created: {Iterations}");
			builder.AppendLine($"Items per ListView ItemsSource: {ItemsPerListView}");
			builder.AppendLine($"Retained TemplatedItems handles per run: {Iterations}");
			builder.AppendLine($"Payload per discarded ListView: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained TemplatedItems handles after clearing TemplatedItemsList._itemsView");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained TemplatedItems handles with MAUI owner reference intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: app TemplatedItems cache -> TemplatedItemsList._itemsView -> discarded ListView -> BindingContext payload");
			builder.AppendLine("Distinct from external ItemsSource/ListProxy subscriptions: the retained object is the owner-created TemplatedItems list handle itself.");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  TemplatedItems handles retained by app cache: {result.RetainedTemplatedItems}");
			builder.AppendLine($"  ListView owners alive after full GC: {result.OwnersAlive}/{Iterations}");
			builder.AppendLine($"  owner payloads alive after full GC: {result.PayloadsAlive}/{Iterations}");
			builder.AppendLine($"  owner payload buffers alive after full GC: {result.PayloadBuffersAlive}/{Iterations}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
