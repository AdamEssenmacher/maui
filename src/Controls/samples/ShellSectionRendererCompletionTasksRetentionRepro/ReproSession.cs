#if MACCATALYST || IOS
using System.Reflection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Platform;
using UIKit;

namespace ShellSectionRendererCompletionTasksRetentionRepro;

public static class ReproSession
{
	const int Iterations = 40;
	const int PayloadPagesPerIteration = 2;
	const int TotalPayloadPages = Iterations * PayloadPagesPerIteration;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo CompletionTasksField =
		typeof(ShellSectionRenderer).GetField("_completionTasks", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(ShellSectionRenderer), "_completionTasks");

	static readonly FieldInfo NavStackField =
		typeof(ShellSection).GetField("_navStack", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(ShellSection), "_navStack");

	public static string ResultsPath => Path.Combine(Path.GetTempPath(), "shellsectionrenderer-completiontasks-retention-results.txt");

	public static async Task<string> RunAsync(IMauiContext context)
	{
		var control = await RunScenarioAsync(context, completePendingBeforeDispose: true);
		var current = await RunScenarioAsync(context, completePendingBeforeDispose: false);
		var leakProved =
			control.PayloadArraysAlive == 0 &&
			current.PayloadArraysAlive == TotalPayloadPages &&
			current.IncompleteCompletionTasks == Iterations;

		return string.Join(Environment.NewLine,
			"ShellSectionRendererCompletionTasksRetentionRepro",
			$"Result path: {ResultsPath}",
			$"Iterations: {Iterations}",
			$"Payload pages per pop-to-root: {PayloadPagesPerIteration}",
			$"Payload per removed page: {FormatBytes(PayloadBytes)}",
			$"Leak proved: {leakProved}",
			string.Empty,
			control.ToReport("control: complete and clear _completionTasks before Dispose()"),
			string.Empty,
			current.ToReport("current: Dispose() leaves _completionTasks unresolved"));
	}

	static async Task<ScenarioResult> RunScenarioAsync(IMauiContext context, bool completePendingBeforeDispose)
	{
		var rootedRenderers = new List<ShellSectionRenderer>(Iterations);
		var payloadPageRefs = new List<WeakReference>(TotalPayloadPages);
		var payloadVmRefs = new List<WeakReference>(TotalPayloadPages);
		var payloadArrayRefs = new List<WeakReference>(TotalPayloadPages);
		var completionTaskRefs = new List<WeakReference>(Iterations);
		var payloadStackEntriesAfterPopToRoot = 0;

		for (var i = 0; i < Iterations; i++)
		{
			var rootPage = new ContentPage { Title = $"Root {i}" };
			var shellContent = new ShellContent { Content = rootPage };
			var shellSection = new ShellSection();
			shellSection.Items.Add(shellContent);

			var shellItem = new FlyoutItem();
			shellItem.Items.Add(shellSection);

			var shell = new Shell();
			shell.Items.Add(shellItem);

			var shellRenderer = (ShellRenderer)shell.ToHandler(context);
			_ = shellRenderer.View;

			var sectionRenderer = new ShellSectionRenderer(shellRenderer)
			{
				ShellSection = shellSection
			};
			_ = sectionRenderer.View;

			var payloadPages = new List<Page> { null! };
			for (var pageIndex = 0; pageIndex < PayloadPagesPerIteration; pageIndex++)
			{
				var payloadPage = new PayloadPage(i, pageIndex, PayloadBytes);
				var payloadVm = (PayloadViewModel)payloadPage.BindingContext;
				var payloadArray = payloadVm.Payload;

				payloadPages.Add(payloadPage);
				payloadPageRefs.Add(new WeakReference(payloadPage));
				payloadVmRefs.Add(new WeakReference(payloadVm));
				payloadArrayRefs.Add(new WeakReference(payloadArray));
			}

			NavStackField.SetValue(shellSection, payloadPages);

			var completion = new TaskCompletionSource<bool>();
			var completionTasks = GetCompletionTasks(sectionRenderer);
			completionTasks[new UIViewController()] = completion;

			((IShellSectionController)shellSection).SendPoppingToRoot(completion.Task);
			await Task.Yield();

			payloadStackEntriesAfterPopToRoot += shellSection.Stack.Count(page => page is PayloadPage);

			if (completePendingBeforeDispose)
			{
				completion.TrySetResult(false);
				completionTasks.Clear();
				await Task.Delay(50);
			}

			sectionRenderer.Dispose();

			rootedRenderers.Add(sectionRenderer);
			completionTaskRefs.Add(new WeakReference(completion.Task));

			rootPage = null!;
			shellContent = null!;
			shellSection = null!;
			shellItem = null!;
			shell = null!;
			shellRenderer = null!;
			sectionRenderer = null!;
			payloadPages = null!;
			completion = null!;
		}

		await Task.Delay(250);
		ForceFullGc();
		await Task.Delay(250);
		ForceFullGc();

		var assignedCompletionTasks = 0;
		var incompleteCompletionTasks = 0;
		foreach (var renderer in rootedRenderers)
		{
			var completionTasks = GetCompletionTasks(renderer);
			assignedCompletionTasks += completionTasks.Count;
			foreach (var source in completionTasks.Values)
			{
				if (!source.Task.IsCompleted)
					incompleteCompletionTasks++;
			}
		}

		return new ScenarioResult(
			assignedCompletionTasks,
			incompleteCompletionTasks,
			payloadStackEntriesAfterPopToRoot,
			CountAlive(completionTaskRefs),
			CountAlive(payloadPageRefs),
			CountAlive(payloadVmRefs),
			CountAlive(payloadArrayRefs),
			(long)CountAlive(payloadArrayRefs) * PayloadBytes,
			GC.GetTotalMemory(forceFullCollection: true));
	}

	static Dictionary<UIViewController, TaskCompletionSource<bool>> GetCompletionTasks(ShellSectionRenderer renderer)
	{
		return (Dictionary<UIViewController, TaskCompletionSource<bool>>)CompletionTasksField.GetValue(renderer)!;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 3; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	static int CountAlive(List<WeakReference> references)
	{
		var count = 0;
		foreach (var reference in references)
		{
			if (reference.IsAlive)
				count++;
		}

		return count;
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024d / 1024d:0.0} MiB";

		if (bytes >= 1024)
			return $"{bytes / 1024d:0.0} KiB";

		return $"{bytes} B";
	}

	sealed record ScenarioResult(
		int AssignedCompletionTasks,
		int IncompleteCompletionTasks,
		int PayloadStackEntriesAfterPopToRoot,
		int CompletionTasksAlive,
		int PayloadPagesAlive,
		int PayloadViewModelsAlive,
		int PayloadArraysAlive,
		long RetainedPayloadBytes,
		long ManagedHeapBytes)
	{
		public string ToReport(string name) => string.Join(Environment.NewLine,
			$"Run: {name}",
			$"  _completionTasks entries still assigned: {AssignedCompletionTasks}/{Iterations}",
			$"  incomplete completion tasks: {IncompleteCompletionTasks}/{Iterations}",
			$"  payload pages still in ShellSection.Stack after SendPoppingToRoot: {PayloadStackEntriesAfterPopToRoot}/{TotalPayloadPages}",
			$"  completion task objects alive after full GC: {CompletionTasksAlive}/{Iterations}",
			$"  payload pages alive after full GC: {PayloadPagesAlive}/{TotalPayloadPages}",
			$"  payload view models alive after full GC: {PayloadViewModelsAlive}/{TotalPayloadPages}",
			$"  payload byte arrays alive after full GC: {PayloadArraysAlive}/{TotalPayloadPages}",
			$"  retained payload bytes: {FormatBytes(RetainedPayloadBytes)}",
			$"  managed heap after full GC: {FormatBytes(ManagedHeapBytes)}");
	}

	sealed class PayloadPage : ContentPage
	{
		public PayloadPage(int iteration, int pageIndex, int payloadBytes)
		{
			Title = $"Removed payload {iteration}-{pageIndex}";
			BindingContext = new PayloadViewModel(payloadBytes);
			Content = new Label { Text = $"Removed payload page {iteration}-{pageIndex}" };
		}
	}

	sealed class PayloadViewModel
	{
		public PayloadViewModel(int payloadBytes)
		{
			Payload = new byte[payloadBytes];
			Array.Fill<byte>(Payload, 0x5A);
		}

		public byte[] Payload { get; }
	}
}
#else
namespace ShellSectionRendererCompletionTasksRetentionRepro;

public static class ReproSession
{
	public static string ResultsPath => Path.Combine(Path.GetTempPath(), "shellsectionrenderer-completiontasks-retention-results.txt");

	public static Task<string> RunAsync(object context)
	{
		return Task.FromResult("This repro is only implemented for Mac Catalyst/iOS.");
	}
}
#endif
