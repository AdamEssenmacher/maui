using System.Collections.Specialized;
using System.Reflection;
using System.Text;
using Microsoft.Maui.Controls;

namespace CorePublicCollectionsRetentionRepro;

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
			Text = "Running core public collections retention repro...",
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
			var text = "CorePublicCollectionsRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/core-public-collections-retention-results.txt";

	const int Iterations = 24;
	const int ItemsAddedThenRemovedPerCollection = 3;
	const int PayloadBytes = 1024 * 1024;
	static readonly CollectionKind[] Kinds = Enum.GetValues<CollectionKind>();

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
		var retainedCollections = new List<object>(Iterations * Kinds.Length);
		var ownerReferences = new List<WeakReference<object>>(Iterations * Kinds.Length);
		var payloadReferences = new List<WeakReference<CollectionOwnerPayload>>(Iterations * Kinds.Length);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations * Kinds.Length);

		for (var i = 0; i < Iterations; i++)
		{
			foreach (var kind in Kinds)
			{
				CreateRetainedCollection(kind, i, clearCollectionHandlers, retainedCollections, ownerReferences, payloadReferences, payloadBufferReferences);
			}
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(ownerReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			retainedCollections.Count,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedCollections);
		return result;
	}

	static void CreateRetainedCollection(
		CollectionKind kind,
		int iteration,
		bool clearCollectionHandlers,
		List<object> retainedCollections,
		List<WeakReference<object>> ownerReferences,
		List<WeakReference<CollectionOwnerPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new CollectionOwnerPayload($"{kind}-owner-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)iteration;

		var owner = CreateOwner(kind, payload);
		var collection = PopulateThenClearCollection(kind, owner, iteration);

		if (clearCollectionHandlers)
			ClearRetainingCollectionEvents(collection);

		retainedCollections.Add(collection);
		ownerReferences.Add(new WeakReference<object>(owner));
		payloadReferences.Add(new WeakReference<CollectionOwnerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));
	}

	static object CreateOwner(CollectionKind kind, CollectionOwnerPayload payload)
	{
		return kind switch
		{
			CollectionKind.PageToolbarItems or CollectionKind.PageMenuBarItems => new ContentPage { BindingContext = payload, Title = payload.Name },
			CollectionKind.CellContextActions => CreateTextCell(payload),
			CollectionKind.PickerItems => new Picker { Title = payload.Name, BindingContext = payload },
			CollectionKind.FormattedStringSpans => new FormattedString { BindingContext = payload },
			CollectionKind.ResourceDictionaryMergedDictionaries => new ResourceDictionary { ["payload"] = payload },
			CollectionKind.ElementEffects => new Label { Text = payload.Name, BindingContext = payload },
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};
	}

	static Cell CreateTextCell(CollectionOwnerPayload payload)
	{
#pragma warning disable CS0618
		return new TextCell { Text = payload.Name, BindingContext = payload };
#pragma warning restore CS0618
	}

	static object PopulateThenClearCollection(CollectionKind kind, object owner, int iteration)
	{
		switch (kind)
		{
			case CollectionKind.PageToolbarItems:
			{
				var collection = ((Page)owner).ToolbarItems;
				for (var i = 0; i < ItemsAddedThenRemovedPerCollection; i++)
					collection.Add(new ToolbarItem { Text = $"Toolbar {iteration}-{i}" });
				RemoveAll(collection);
				return collection;
			}
			case CollectionKind.PageMenuBarItems:
			{
				var collection = ((Page)owner).MenuBarItems;
				for (var i = 0; i < ItemsAddedThenRemovedPerCollection; i++)
					collection.Add(new MenuBarItem { Text = $"Menu {iteration}-{i}" });
				RemoveAll(collection);
				return collection;
			}
			case CollectionKind.CellContextActions:
			{
				var collection = ((Cell)owner).ContextActions;
				for (var i = 0; i < ItemsAddedThenRemovedPerCollection; i++)
					collection.Add(new MenuItem { Text = $"Action {iteration}-{i}" });
				RemoveAll(collection);
				return collection;
			}
			case CollectionKind.PickerItems:
			{
				var collection = ((Picker)owner).Items;
				for (var i = 0; i < ItemsAddedThenRemovedPerCollection; i++)
					collection.Add($"Choice {iteration}-{i}");
				RemoveAll(collection);
				return collection;
			}
			case CollectionKind.FormattedStringSpans:
			{
				var collection = ((FormattedString)owner).Spans;
				for (var i = 0; i < ItemsAddedThenRemovedPerCollection; i++)
					collection.Add(new Span { Text = $"Span {iteration}-{i}" });
				RemoveAll(collection);
				return collection;
			}
			case CollectionKind.ResourceDictionaryMergedDictionaries:
			{
				var collection = ((ResourceDictionary)owner).MergedDictionaries;
				var added = new List<ResourceDictionary>();
				for (var i = 0; i < ItemsAddedThenRemovedPerCollection; i++)
				{
					var dictionary = new ResourceDictionary { ["index"] = $"{iteration}-{i}" };
					added.Add(dictionary);
					collection.Add(dictionary);
				}

				foreach (var dictionary in added)
					collection.Remove(dictionary);
				return collection;
			}
			case CollectionKind.ElementEffects:
				return ((Element)owner).Effects;
			default:
				throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
		}
	}

	static void RemoveAll<T>(IList<T> collection)
	{
		while (collection.Count > 0)
			collection.RemoveAt(0);
	}

	static void ClearRetainingCollectionEvents(object collection)
	{
		ClearEventFieldsRecursive(collection, new HashSet<object>(ReferenceEqualityComparer.Instance));
	}

	static void ClearEventFieldsRecursive(object value, HashSet<object> visited)
	{
		if (!visited.Add(value))
			return;

		ClearEventField(value, "CollectionChanged", typeof(NotifyCollectionChangedEventHandler));
		ClearEventField(value, "Clearing", typeof(EventHandler));

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

				if (nested is INotifyCollectionChanged || field.Name is "_list")
					ClearEventFieldsRecursive(nested, visited);
			}

			type = type.BaseType;
		}
	}

	static void ClearEventField(object target, string eventName, Type eventHandlerType)
	{
		var type = target.GetType();
		while (type is not null)
		{
			var field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (field is not null && eventHandlerType.IsAssignableFrom(field.FieldType))
			{
				field.SetValue(target, null);
				return;
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

	enum CollectionKind
	{
		PageToolbarItems,
		PageMenuBarItems,
		CellContextActions,
		PickerItems,
		FormattedStringSpans,
		ResourceDictionaryMergedDictionaries,
		ElementEffects
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

		public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
	}

	public readonly record struct ScenarioResult(
		int OwnersAlive,
		int PayloadsAlive,
		int PayloadBuffersAlive,
		int RetainedCollections,
		long HeapBefore,
		long HeapAfter)
	{
		public long HeapDelta => HeapAfter - HeapBefore;
	}

	public readonly record struct ReproReport(ScenarioResult Control, ScenarioResult Current)
	{
		static int ExpectedOwners => Iterations * Kinds.Length;

		public bool LeakProved =>
			Control.OwnersAlive == 0 &&
			Control.PayloadsAlive == 0 &&
			Control.PayloadBuffersAlive == 0 &&
			Current.OwnersAlive == ExpectedOwners &&
			Current.PayloadsAlive == ExpectedOwners &&
			Current.PayloadBuffersAlive == ExpectedOwners;

		public string ToText()
		{
			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("CorePublicCollectionsRetentionRepro");
			builder.AppendLine($"Iterations per collection surface: {Iterations}");
			builder.AppendLine($"Collection surfaces: {string.Join(", ", Kinds)}");
			builder.AppendLine($"Items added then removed per non-empty collection: {ItemsAddedThenRemovedPerCollection}");
			builder.AppendLine($"Retained public collections per run: {ExpectedOwners}");
			builder.AppendLine($"Payload per discarded owner: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained public collections after clearing collection event fields");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained public collections with MAUI collection event handlers intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: app public collection cache -> owner-created collection event field -> discarded owner -> BindingContext or resource payload");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  public collections retained by app cache: {result.RetainedCollections}");
			builder.AppendLine($"  owners alive after full GC: {result.OwnersAlive}/{ExpectedOwners}");
			builder.AppendLine($"  owner payloads alive after full GC: {result.PayloadsAlive}/{ExpectedOwners}");
			builder.AppendLine($"  owner payload buffers alive after full GC: {result.PayloadBuffersAlive}/{ExpectedOwners}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
