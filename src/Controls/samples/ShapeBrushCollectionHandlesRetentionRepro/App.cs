using System.Collections.Specialized;
using System.Reflection;
using System.Text;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace ShapeBrushCollectionHandlesRetentionRepro;

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
			Text = "Running shape/brush collection handle retention repro...",
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
			var text = "ShapeBrushCollectionHandlesRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/shape-brush-collection-handles-retention-results.txt";

	const int Iterations = 32;
	const int ItemsAddedThenRemovedPerCollection = 3;
	const int PayloadBytes = 1024 * 1024;
	static readonly CollectionKind[] Kinds = Enum.GetValues<CollectionKind>();

	static readonly BindableProperty PayloadProperty = BindableProperty.CreateAttached(
		"Payload",
		typeof(CollectionOwnerPayload),
		typeof(ReproSession),
		null);

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
		var ownerReferences = new List<WeakReference<BindableObject>>(Iterations * Kinds.Length);
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
		List<WeakReference<BindableObject>> ownerReferences,
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
		ownerReferences.Add(new WeakReference<BindableObject>(owner));
		payloadReferences.Add(new WeakReference<CollectionOwnerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));
	}

	static BindableObject CreateOwner(CollectionKind kind, CollectionOwnerPayload payload)
	{
		BindableObject owner = kind switch
		{
			CollectionKind.GradientBrushGradientStops => new LinearGradientBrush(),
			CollectionKind.PathFigureSegments => new PathFigure { StartPoint = new Point(0, 0) },
			CollectionKind.PathGeometryFigures => new PathGeometry(),
			CollectionKind.GeometryGroupChildren => new GeometryGroup(),
			CollectionKind.TransformGroupChildren => new TransformGroup(),
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};

		owner.SetValue(PayloadProperty, payload);
		return owner;
	}

	static object PopulateThenClearCollection(CollectionKind kind, BindableObject owner, int iteration)
	{
		switch (kind)
		{
			case CollectionKind.GradientBrushGradientStops:
			{
				var collection = ((GradientBrush)owner).GradientStops;
				for (var i = 0; i < ItemsAddedThenRemovedPerCollection; i++)
					collection.Add(new GradientStop(Colors.CornflowerBlue, i / (float)ItemsAddedThenRemovedPerCollection));
				RemoveAll(collection);
				return collection;
			}
			case CollectionKind.PathFigureSegments:
			{
				var collection = ((PathFigure)owner).Segments;
				for (var i = 0; i < ItemsAddedThenRemovedPerCollection; i++)
					collection.Add(new LineSegment { Point = new Point(iteration + i + 1, iteration + i + 2) });
				RemoveAll(collection);
				return collection;
			}
			case CollectionKind.PathGeometryFigures:
			{
				var collection = ((PathGeometry)owner).Figures;
				for (var i = 0; i < ItemsAddedThenRemovedPerCollection; i++)
					collection.Add(CreatePathFigure(iteration, i));
				RemoveAll(collection);
				return collection;
			}
			case CollectionKind.GeometryGroupChildren:
			{
				var collection = ((GeometryGroup)owner).Children;
				for (var i = 0; i < ItemsAddedThenRemovedPerCollection; i++)
					collection.Add(new LineGeometry(new Point(i, iteration), new Point(i + 1, iteration + 1)));
				RemoveAll(collection);
				return collection;
			}
			case CollectionKind.TransformGroupChildren:
			{
				var collection = ((TransformGroup)owner).Children;
				for (var i = 0; i < ItemsAddedThenRemovedPerCollection; i++)
					collection.Add(new ScaleTransform(1 + i * 0.01, 1 + i * 0.01));
				RemoveAll(collection);
				return collection;
			}
			default:
				throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
		}
	}

	static PathFigure CreatePathFigure(int iteration, int index)
	{
		return new PathFigure
		{
			StartPoint = new Point(iteration, index),
			Segments =
			{
				new LineSegment { Point = new Point(iteration + index + 1, index + 1) }
			}
		};
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
		GradientBrushGradientStops,
		PathFigureSegments,
		PathGeometryFigures,
		GeometryGroupChildren,
		TransformGroupChildren
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
			builder.AppendLine("ShapeBrushCollectionHandlesRetentionRepro");
			builder.AppendLine($"Iterations per collection surface: {Iterations}");
			builder.AppendLine($"Collection surfaces: {string.Join(", ", Kinds)}");
			builder.AppendLine($"Items added then removed per collection: {ItemsAddedThenRemovedPerCollection}");
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
			builder.AppendLine("Leak path: app public collection cache -> owner-created collection event field -> discarded brush/geometry/transform owner -> attached payload");
			builder.AppendLine("Distinct from shared-child and Clear()/Reset leaks: child items are removed individually before retaining the empty collection handles.");
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
