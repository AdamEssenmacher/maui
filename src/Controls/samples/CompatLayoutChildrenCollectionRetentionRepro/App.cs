using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reflection;
using System.Text;
using Microsoft.Maui.Controls;
using Compat = Microsoft.Maui.Controls.Compatibility;

namespace CompatLayoutChildrenCollectionRetentionRepro;

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
			Text = "Running compatibility layout Children collection retention repro...",
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
			var text = "CompatLayoutChildrenCollectionRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/compat-layout-children-collection-retention-results.txt";

	const int Iterations = 32;
	const int ItemsAddedThenRemovedPerCollection = 3;
	const int PayloadBytes = 1024 * 1024;

	static readonly LayoutKind[] Kinds = Enum.GetValues<LayoutKind>();

	public static ReproReport Run()
	{
		var control = RunScenario(clearRetainingFields: true);
		var current = RunScenario(clearRetainingFields: false);

		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(bool clearRetainingFields)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
		var retainedCollections = new List<object>(Iterations * Kinds.Length);
		var ownerReferences = new List<WeakReference<BindableObject>>(Iterations * Kinds.Length);
		var payloadReferences = new List<WeakReference<CollectionOwnerPayload>>(Iterations * Kinds.Length);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations * Kinds.Length);

		for (var i = 0; i < Iterations; i++)
		{
			foreach (var kind in Kinds)
				CreateRetainedCollection(kind, i, clearRetainingFields, retainedCollections, ownerReferences, payloadReferences, payloadBufferReferences);
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
		LayoutKind kind,
		int iteration,
		bool clearRetainingFields,
		List<object> retainedCollections,
		List<WeakReference<BindableObject>> ownerReferences,
		List<WeakReference<CollectionOwnerPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new CollectionOwnerPayload($"{kind}-owner-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)iteration;

		var owner = CreateOwner(kind, payload);
		var collection = GetChildren(kind, owner);

		for (var i = 0; i < ItemsAddedThenRemovedPerCollection; i++)
			collection.Add(new Label { Text = $"{kind} child {iteration}-{i}" });

		RemoveAll(collection);

		if (clearRetainingFields)
			ClearCollectionOwnerRoots(collection);

		retainedCollections.Add(collection);
		ownerReferences.Add(new WeakReference<BindableObject>(owner));
		payloadReferences.Add(new WeakReference<CollectionOwnerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));
	}

	static BindableObject CreateOwner(LayoutKind kind, CollectionOwnerPayload payload)
	{
		BindableObject owner = kind switch
		{
			LayoutKind.StackLayout => new Compat.StackLayout(),
			LayoutKind.FlexLayout => new Compat.FlexLayout(),
			LayoutKind.Grid => new Compat.Grid(),
			LayoutKind.AbsoluteLayout => new Compat.AbsoluteLayout(),
			LayoutKind.RelativeLayout => new Compat.RelativeLayout(),
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};

		owner.BindingContext = payload;
		return owner;
	}

	static IList<View> GetChildren(LayoutKind kind, BindableObject owner)
	{
		return kind switch
		{
			LayoutKind.StackLayout => ((Compat.StackLayout)owner).Children,
			LayoutKind.FlexLayout => ((Compat.FlexLayout)owner).Children,
			LayoutKind.Grid => ((Compat.Grid)owner).Children,
			LayoutKind.AbsoluteLayout => ((Compat.AbsoluteLayout)owner).Children,
			LayoutKind.RelativeLayout => ((Compat.RelativeLayout)owner).Children,
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};
	}

	static void RemoveAll<T>(IList<T> collection)
	{
		while (collection.Count > 0)
			collection.RemoveAt(0);
	}

	static void ClearCollectionOwnerRoots(object collection)
	{
		ClearBindableParentFields(collection);
		ClearCollectionEventFieldsRecursive(collection, new HashSet<object>(ReferenceEqualityComparer.Instance));
	}

	static void ClearBindableParentFields(object value)
	{
		var type = value.GetType();
		while (type is not null)
		{
			foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
			{
				if (!field.Name.Contains("Parent", StringComparison.Ordinal))
					continue;
				if (!typeof(BindableObject).IsAssignableFrom(field.FieldType))
					continue;

				field.SetValue(value, null);
			}

			type = type.BaseType;
		}
	}

	static void ClearCollectionEventFieldsRecursive(object value, HashSet<object> visited)
	{
		if (!visited.Add(value))
			return;

		ClearEventField(value, "CollectionChanged", typeof(NotifyCollectionChangedEventHandler));

		var type = value.GetType();
		while (type is not null)
		{
			foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
			{
				if (field.FieldType == typeof(string) || field.FieldType.IsValueType)
					continue;

				var nested = field.GetValue(value);
				if (nested is INotifyCollectionChanged)
					ClearCollectionEventFieldsRecursive(nested, visited);
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

	enum LayoutKind
	{
		StackLayout,
		FlexLayout,
		Grid,
		AbsoluteLayout,
		RelativeLayout
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
			builder.AppendLine("CompatLayoutChildrenCollectionRetentionRepro");
			builder.AppendLine($"Iterations per collection surface: {Iterations}");
			builder.AppendLine($"Collection surfaces: {string.Join(", ", Kinds)}");
			builder.AppendLine($"Items added then removed per collection: {ItemsAddedThenRemovedPerCollection}");
			builder.AppendLine($"Retained empty public Children wrappers per run: {ExpectedOwners}");
			builder.AppendLine($"Payload per discarded owner: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained empty Children wrappers after clearing wrapper parent fields and collection event fields");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained empty Children wrappers with MAUI owner links intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: app public Children wrapper cache -> wrapper/internal collection owner link -> discarded compatibility layout -> BindingContext payload");
			builder.AppendLine("Distinct from layout Children.Clear()/Reset leaks: children are removed individually before retaining the empty wrappers.");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  public Children wrappers retained by app cache: {result.RetainedCollections}");
			builder.AppendLine($"  owners alive after full GC: {result.OwnersAlive}/{ExpectedOwners}");
			builder.AppendLine($"  owner payloads alive after full GC: {result.PayloadsAlive}/{ExpectedOwners}");
			builder.AppendLine($"  owner payload buffers alive after full GC: {result.PayloadBuffersAlive}/{ExpectedOwners}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
