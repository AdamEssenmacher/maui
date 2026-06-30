using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;

namespace TableViewModelOwnerRetentionRepro;

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
			Text = "Running TableView model owner retention repro...",
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
			var text = "TableViewModelOwnerRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/tableview-model-owner-retention-results.txt";

	const int Iterations = 160;
	const int SectionsPerTable = 4;
	const int CellsPerSection = 6;
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
		var retainedModels = new List<TableModel>(Iterations);
		var ownerReferences = new List<WeakReference<TableView>>(Iterations);
		var payloadReferences = new List<WeakReference<TableOwnerPayload>>(Iterations);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			CreateRetainedModel(clearOwnerReference, i, retainedModels, ownerReferences, payloadReferences, payloadBufferReferences);
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			CountAlive(ownerReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			retainedModels.Count,
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedModels);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRetainedModel(
		bool clearOwnerReference,
		int iteration,
		List<TableModel> retainedModels,
		List<WeakReference<TableView>> ownerReferences,
		List<WeakReference<TableOwnerPayload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
#pragma warning disable CS0618
		var table = new TableView(CreateRealisticRoot(iteration));
#pragma warning restore CS0618
		var payload = new TableOwnerPayload($"table-view-model-{iteration}", new byte[PayloadBytes]);
		payload.Buffer[0] = (byte)iteration;
		table.BindingContext = payload;

		var model = table.Model;

		// Remove row/cell graphs so this repro isolates the TableSectionModel._parent path
		// instead of the separately tracked TableView cell parent-retention class.
		table.Root = new TableRoot();

		if (clearOwnerReference)
			ClearOwnerReferences(model, table);

		retainedModels.Add(model);
		ownerReferences.Add(new WeakReference<TableView>(table));
		payloadReferences.Add(new WeakReference<TableOwnerPayload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		table = null!;
		model = null!;
		payload = null!;
	}

	static TableRoot CreateRealisticRoot(int iteration)
	{
		var root = new TableRoot($"Customer order batch {iteration}");

		for (var sectionIndex = 0; sectionIndex < SectionsPerTable; sectionIndex++)
		{
			var section = new TableSection($"Region {sectionIndex + 1}");
			for (var cellIndex = 0; cellIndex < CellsPerSection; cellIndex++)
			{
				section.Add(new TextCell
				{
					Text = $"Order {iteration:000}-{sectionIndex:00}-{cellIndex:00}",
					Detail = $"Priority {(cellIndex % 3) + 1}; warehouse {sectionIndex + 1}"
				});
			}

			root.Add(section);
		}

		return root;
	}

	static void ClearOwnerReferences(TableModel model, TableView owner)
	{
		var parentField = model.GetType().GetField("_parent", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException($"Could not find TableSectionModel._parent on {model.GetType().FullName}.");
		var rootField = model.GetType().GetField("_root", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException($"Could not find TableSectionModel._root on {model.GetType().FullName}.");

		if (!ReferenceEquals(parentField.GetValue(model), owner))
			throw new InvalidOperationException("TableSectionModel._parent did not reference the expected TableView owner.");

		parentField.SetValue(model, null);
		rootField.SetValue(model, new TableRoot());

		if (parentField.GetValue(model) is not null)
			throw new InvalidOperationException("TableSectionModel._parent remained assigned after reflection clear.");
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

	sealed class TableOwnerPayload
	{
		public TableOwnerPayload(string name, byte[] buffer)
		{
			Name = name;
			Buffer = buffer;
		}

		public string Name { get; }

		public byte[] Buffer { get; }
	}

	public readonly record struct ScenarioResult(
		int OwnersAlive,
		int PayloadsAlive,
		int PayloadBuffersAlive,
		int RetainedModels,
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
			builder.AppendLine("TableViewModelOwnerRetentionRepro");
			builder.AppendLine($"TableView owners created: {Iterations}");
			builder.AppendLine($"Initial realistic table shape: {SectionsPerTable} sections x {CellsPerSection} TextCell rows");
			builder.AppendLine($"Retained public/internal TableModel handles per run: {Iterations}");
			builder.AppendLine($"Payload per discarded TableView BindingContext: {PayloadBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Run: control: retained TableModel handles after clearing TableSectionModel owner links");
			AppendScenario(builder, Control);
			builder.AppendLine();
			builder.AppendLine("Run: current: retained TableModel handles with MAUI owner references intact");
			AppendScenario(builder, Current);
			builder.AppendLine($"  retained payload bytes: {retainedBytes / 1024d / 1024d:0.0} MiB");
			builder.AppendLine();
			builder.AppendLine("Leak path: app/renderer/diagnostics TableModel cache -> TableSectionModel._parent and _root owner event delegates -> discarded TableView -> BindingContext payload");
			builder.AppendLine("Distinct from shared TableRoot, TableViewModelRenderer, and removed-cell leaks: rows are reset before GC and the retained object is the owner-created TableModel handle itself.");
			builder.AppendLine($"dotnet-version: {Environment.Version}");
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine($"  TableModel handles retained by app cache: {result.RetainedModels}");
			builder.AppendLine($"  TableView owners alive after full GC: {result.OwnersAlive}/{Iterations}");
			builder.AppendLine($"  owner BindingContext payloads alive after full GC: {result.PayloadsAlive}/{Iterations}");
			builder.AppendLine($"  owner payload buffers alive after full GC: {result.PayloadBuffersAlive}/{Iterations}");
			builder.AppendLine($"  managed heap delta: {result.HeapDelta / 1024d / 1024d:0.0} MiB");
		}
	}
}
