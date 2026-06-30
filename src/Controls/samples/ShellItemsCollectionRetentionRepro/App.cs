using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;

namespace ShellItemsCollectionRetentionRepro;

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
			Text = "Running Shell Items collection retention repro...",
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
			var text = "ShellItemsCollectionRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/shell-items-collection-retention-results.txt";

	const int OwnersPerSurface = 54;
	const int OwnerSurfaces = 3;
	const int ChildItemsAddedThenRemovedPerOwner = 3;
	const int PayloadBytes = 1024 * 1024;
	const int TotalOwners = OwnersPerSurface * OwnerSurfaces;

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
		var retainedItemsCollections = new List<object>(TotalOwners);
		var ownerReferences = new List<WeakReference<BindableObject>>(TotalOwners);
		var payloadReferences = new List<WeakReference<CollectionOwnerPayload>>(TotalOwners);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(TotalOwners);

		for (var i = 0; i < OwnersPerSurface; i++)
		{
			CreateRetainedItemsCollection(OwnerKind.Shell, i, clearCollectionHandlers, retainedItemsCollections, ownerReferences, payloadReferences, payloadBufferReferences);
			CreateRetainedItemsCollection(OwnerKind.ShellItem, i, clearCollectionHandlers, retainedItemsCollections, ownerReferences, payloadReferences, payloadBufferReferences);
			CreateRetainedItemsCollection(OwnerKind.ShellSection, i, clearCollectionHandlers, retainedItemsCollections, ownerReferences, payloadReferences, payloadBufferReferences);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(ownerReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			retainedItemsCollections.Count,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedItemsCollections);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRetainedItemsCollection(
		OwnerKind kind,
		int iteration,
		bool clearCollectionHandlers,
		List<object> retainedItemsCollections,
		List<WeakReference<BindableObject>> ownerReferences,
		List<WeakReference<CollectionOwnerPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new CollectionOwnerPayload($"{kind}-owner-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)(iteration + (int)kind);

		BindableObject owner;
		object items;

		switch (kind)
		{
			case OwnerKind.Shell:
			{
				var shell = new Shell
				{
					Title = $"Shell {iteration}",
					BindingContext = payload
				};

				var shellItems = shell.Items;
				for (var i = 0; i < ChildItemsAddedThenRemovedPerOwner; i++)
					shellItems.Add(new ShellItem { Title = $"Area {iteration}-{i}" });

				while (shellItems.Count > 0)
					shellItems.RemoveAt(0);

				owner = shell;
				items = shellItems;
				break;
			}
			case OwnerKind.ShellItem:
			{
				var shellItem = new ShellItem
				{
					Title = $"ShellItem {iteration}",
					BindingContext = payload
				};

				var sections = shellItem.Items;
				for (var i = 0; i < ChildItemsAddedThenRemovedPerOwner; i++)
					sections.Add(new ShellSection { Title = $"Tab {iteration}-{i}" });

				while (sections.Count > 0)
					sections.RemoveAt(0);

				owner = shellItem;
				items = sections;
				break;
			}
			default:
			{
				var shellSection = new ShellSection
				{
					Title = $"ShellSection {iteration}",
					BindingContext = payload
				};

				var contents = shellSection.Items;
				for (var i = 0; i < ChildItemsAddedThenRemovedPerOwner; i++)
				{
					contents.Add(new ShellContent
					{
						Title = $"Customer {iteration}-{i}",
						Content = new ContentPage
						{
							Title = $"Customer {iteration}-{i}",
							Content = new Label { Text = $"Customer {iteration}-{i}" }
						}
					});
				}

				while (contents.Count > 0)
					contents.RemoveAt(0);

				owner = shellSection;
				items = contents;
				break;
			}
		}

		if (clearCollectionHandlers)
			ClearRetainingCollectionEvents(items);

		retainedItemsCollections.Add(items);
		ownerReferences.Add(new WeakReference<BindableObject>(owner));
		payloadReferences.Add(new WeakReference<CollectionOwnerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		owner = null!;
		payload = null!;
		items = null!;
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

	enum OwnerKind
	{
		Shell,
		ShellItem,
		ShellSection
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
		int RetainedItemsCollections,
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
			Current.OwnersAlive == TotalOwners &&
			Current.PayloadsAlive == TotalOwners &&
			Current.PayloadBuffersAlive == TotalOwners;

		public string ToText()
		{
			var retainedBytes = Current.PayloadBuffersAlive * PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("ShellItemsCollectionRetentionRepro");
			builder.AppendLine($"Shell owner surfaces: {OwnerSurfaces} (Shell.Items, ShellItem.Items, ShellSection.Items)");
			builder.AppendLine($"Owners created per surface: {OwnersPerSurface}");
			builder.AppendLine($"Total Shell owners created: {TotalOwners}");
			builder.AppendLine($"Child items added then removed per owner: {ChildItemsAddedThenRemovedPerOwner}");
			builder.AppendLine($"Retained Items collections per run: {TotalOwners}");
			builder.AppendLine($"Payload per discarded Shell owner: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained Items collections after clearing nested collection event fields");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained Items collections with MAUI collection event handlers intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: app Items collection cache -> ShellElementCollection/DeclaredChildren collection events -> discarded Shell owner -> BindingContext payload");
			builder.AppendLine("Distinct from Shell.Items.Clear() handler-disconnect leaks: child Shell elements are removed individually before retaining the empty Items wrappers.");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  Items collections retained by app cache: {result.RetainedItemsCollections}");
			builder.AppendLine($"  Shell owners alive after full GC: {result.OwnersAlive}/{TotalOwners}");
			builder.AppendLine($"  owner payloads alive after full GC: {result.PayloadsAlive}/{TotalOwners}");
			builder.AppendLine($"  owner payload buffers alive after full GC: {result.PayloadBuffersAlive}/{TotalOwners}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
