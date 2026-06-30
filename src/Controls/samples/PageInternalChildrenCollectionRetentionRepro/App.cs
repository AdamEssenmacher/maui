using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;

namespace PageInternalChildrenCollectionRetentionRepro;

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
			Text = "Running Page.InternalChildren collection retention repro...",
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
			var text = "PageInternalChildrenCollectionRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/page-internalchildren-collection-retention-results.txt";

	const int Iterations = 160;
	const int ChildrenAddedThenRemovedPerPage = 3;
	const int PayloadBytes = 1024 * 1024;

	public static ReproReport Run()
	{
		var control = RunScenario(clearCollectionHandlers: true);
		var current = RunScenario(clearCollectionHandlers: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearCollectionHandlers)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedInternalChildrenCollections = new List<object>(Iterations);
		var ownerReferences = new List<WeakReference<ContentPage>>(Iterations);
		var payloadReferences = new List<WeakReference<CollectionOwnerPayload>>(Iterations);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations);

		for (var i = 0; i < Iterations; i++)
			CreateRetainedInternalChildrenCollection(i, clearCollectionHandlers, retainedInternalChildrenCollections, ownerReferences, payloadReferences, payloadBufferReferences);

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(ownerReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			retainedInternalChildrenCollections.Count,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedInternalChildrenCollections);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRetainedInternalChildrenCollection(
		int iteration,
		bool clearCollectionHandlers,
		List<object> retainedInternalChildrenCollections,
		List<WeakReference<ContentPage>> ownerReferences,
		List<WeakReference<CollectionOwnerPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new CollectionOwnerPayload($"content-page-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)iteration;

		var owner = new ContentPage
		{
			Title = $"Customer Page {iteration}",
			BindingContext = payload
		};

		var internalChildren = owner.InternalChildren;
		for (var i = 0; i < ChildrenAddedThenRemovedPerPage; i++)
		{
			internalChildren.Add(new Label
			{
				Text = $"Child view {iteration}-{i}",
				AutomationId = $"child-{iteration}-{i}"
			});
		}

		while (internalChildren.Count > 0)
			internalChildren.RemoveAt(0);

		if (clearCollectionHandlers)
			ClearRetainingCollectionEvents(internalChildren);

		retainedInternalChildrenCollections.Add(internalChildren);
		ownerReferences.Add(new WeakReference<ContentPage>(owner));
		payloadReferences.Add(new WeakReference<CollectionOwnerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		owner = null!;
		payload = null!;
		internalChildren = null!;
	}

	static void ClearRetainingCollectionEvents(object collection)
	{
		ClearEventFieldsRecursive(collection, new HashSet<object>(ReferenceEqualityComparer.Instance));
	}

	static void ClearEventFieldsRecursive(object value, HashSet<object> visited)
	{
		if (!visited.Add(value))
			return;

		ClearNotifyCollectionChangedHandlerFields(value);

		var type = value.GetType();
		while (type is not null)
		{
			foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
			{
				if (field.FieldType == typeof(string) || field.FieldType.IsValueType)
					continue;

				var nested = field.GetValue(value);
				if (nested is null)
					continue;

				if (nested is INotifyCollectionChanged)
					ClearEventFieldsRecursive(nested, visited);
			}

			type = type.BaseType;
		}
	}

	static void ClearNotifyCollectionChangedHandlerFields(object target)
	{
		var type = target.GetType();
		while (type is not null)
		{
			foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
			{
				if (typeof(NotifyCollectionChangedEventHandler).IsAssignableFrom(field.FieldType))
					field.SetValue(target, null);
			}

			type = type.BaseType;
		}
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

	sealed class CollectionOwnerPayload
	{
		public CollectionOwnerPayload(string name, byte[] buffer)
		{
			Name = name;
			Buffer = buffer;
		}

		public string Name { get; }

		public byte[] Buffer { get; }
	}

	sealed class ReferenceEqualityComparer : IEqualityComparer<object>
	{
		public static readonly ReferenceEqualityComparer Instance = new();

		public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

		public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
	}

	public readonly record struct ScenarioResult(
		int OwnersAlive,
		int PayloadsAlive,
		int PayloadBuffersAlive,
		int RetainedInternalChildrenCollections,
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
			builder.AppendLine("PageInternalChildrenCollectionRetentionRepro");
			builder.AppendLine($"ContentPage owners created: {Iterations}");
			builder.AppendLine($"InternalChildren added then removed per owner: {ChildrenAddedThenRemovedPerPage}");
			builder.AppendLine($"Retained InternalChildren collections per run: {Iterations}");
			builder.AppendLine($"Payload per discarded ContentPage: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained InternalChildren collections after clearing collection event fields");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained InternalChildren collections with MAUI collection event handlers intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: app InternalChildren collection cache -> InternalChildren.CollectionChanged -> Page.InternalChildrenOnCollectionChanged -> discarded ContentPage -> BindingContext payload");
			builder.AppendLine("Distinct from MultiPage reset and Children wrapper leaks: child views are removed individually before retaining the empty InternalChildren collections.");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  InternalChildren collections retained by app cache: {result.RetainedInternalChildrenCollections}");
			builder.AppendLine($"  ContentPage owners alive after full GC: {result.OwnersAlive}/{Iterations}");
			builder.AppendLine($"  owner payloads alive after full GC: {result.PayloadsAlive}/{Iterations}");
			builder.AppendLine($"  owner payload buffers alive after full GC: {result.PayloadBuffersAlive}/{Iterations}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
