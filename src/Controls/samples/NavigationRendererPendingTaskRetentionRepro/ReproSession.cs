#if MACCATALYST || IOS
using System.Reflection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Platform;

namespace NavigationRendererPendingTaskRetentionRepro;

public static class ReproSession
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly MethodInfo CompletePendingNavigationMethod =
		typeof(NavigationRenderer).GetMethod("CompletePendingNavigation", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(nameof(NavigationRenderer), "CompletePendingNavigation");

	static readonly PropertyInfo CurrentNavigationTaskProperty =
		typeof(NavigationPage).GetProperty("CurrentNavigationTask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(NavigationPage), "CurrentNavigationTask");

	public static string ResultsPath => Path.Combine(Path.GetTempPath(), "navigationrenderer-pending-task-retention-results.txt");

	public static async Task<string> RunAsync(IMauiContext context)
	{
		var control = await RunScenarioAsync(context, completePendingBeforeDispose: true);
		var current = await RunScenarioAsync(context, completePendingBeforeDispose: false);
		var leakProved =
			control.PayloadArraysAlive == 0 &&
			current.PayloadArraysAlive == Iterations &&
			current.IncompleteNavigationTasks == Iterations;

		return string.Join(Environment.NewLine,
			"NavigationRendererPendingTaskRetentionRepro",
			$"Result path: {ResultsPath}",
			$"Iterations: {Iterations}",
			$"Payload per pending navigation page: {FormatBytes(PayloadBytes)}",
			$"Leak proved: {leakProved}",
			string.Empty,
			control.ToReport("control: CompletePendingNavigation(false) before Dispose()"),
			string.Empty,
			current.ToReport("current: Dispose() leaves pending navigation unresolved"));
	}

	static async Task<ScenarioResult> RunScenarioAsync(IMauiContext context, bool completePendingBeforeDispose)
	{
		var rootedNavigationPages = new List<NavigationPage>(Iterations);
		var payloadPageRefs = new List<WeakReference>(Iterations);
		var payloadVmRefs = new List<WeakReference>(Iterations);
		var payloadArrayRefs = new List<WeakReference>(Iterations);
		var rendererRefs = new List<WeakReference>(Iterations);
		var taskRefs = new List<WeakReference>(Iterations);
		var pendingStarted = 0;
		var completedBeforeDispose = 0;

		for (var i = 0; i < Iterations; i++)
		{
			var rootPage = new ContentPage { Title = $"Root {i}" };
			var navigationPage = new NavigationPage(rootPage);
			var renderer = (NavigationRenderer)navigationPage.ToHandler(context);

			_ = renderer.View;
			CompletePendingNavigation(renderer, success: false);
			await Task.Yield();

			var payloadPage = new PayloadPage(i, PayloadBytes);
			var payloadVm = (PayloadViewModel)payloadPage.BindingContext;
			var payloadArray = payloadVm.Payload;

			var task = renderer.PushPageAsync(payloadPage, animated: true);
			await Task.Yield();

			if (!task.IsCompleted)
				pendingStarted++;
			else
				completedBeforeDispose++;

			CurrentNavigationTaskProperty.SetValue(navigationPage, task);

			if (completePendingBeforeDispose)
			{
				CompletePendingNavigation(renderer, success: false);
				await Task.Yield();
			}

			renderer.Dispose();

			rootedNavigationPages.Add(navigationPage);
			payloadPageRefs.Add(new WeakReference(payloadPage));
			payloadVmRefs.Add(new WeakReference(payloadVm));
			payloadArrayRefs.Add(new WeakReference(payloadArray));
			rendererRefs.Add(new WeakReference(renderer));
			taskRefs.Add(new WeakReference(task));

			rootPage = null!;
			navigationPage = null!;
			renderer = null!;
			payloadPage = null!;
			payloadVm = null!;
			payloadArray = null!;
			task = null!;
		}

		await Task.Delay(250);
		ForceFullGc();
		await Task.Delay(250);
		ForceFullGc();

		var incompleteTasks = 0;
		foreach (var navigationPage in rootedNavigationPages)
		{
			if (CurrentNavigationTaskProperty.GetValue(navigationPage) is Task task && !task.IsCompleted)
				incompleteTasks++;
		}

		return new ScenarioResult(
			pendingStarted,
			completedBeforeDispose,
			incompleteTasks,
			CountAlive(rendererRefs),
			CountAlive(taskRefs),
			CountAlive(payloadPageRefs),
			CountAlive(payloadVmRefs),
			CountAlive(payloadArrayRefs),
			(long)CountAlive(payloadArrayRefs) * PayloadBytes,
			GC.GetTotalMemory(forceFullCollection: true));
	}

	static void CompletePendingNavigation(NavigationRenderer renderer, bool success)
	{
		CompletePendingNavigationMethod.Invoke(renderer, new object[] { success });
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
		int PendingStarted,
		int CompletedBeforeDispose,
		int IncompleteNavigationTasks,
		int RenderersAlive,
		int TasksAlive,
		int PayloadPagesAlive,
		int PayloadViewModelsAlive,
		int PayloadArraysAlive,
		long RetainedPayloadBytes,
		long ManagedHeapBytes)
	{
		public string ToReport(string name) => string.Join(Environment.NewLine,
			$"Run: {name}",
			$"  pending navigation tasks started: {PendingStarted}/{Iterations}",
			$"  tasks already completed before dispose: {CompletedBeforeDispose}/{Iterations}",
			$"  live NavigationPages with incomplete CurrentNavigationTask: {IncompleteNavigationTasks}/{Iterations}",
			$"  renderers alive after full GC: {RenderersAlive}/{Iterations}",
			$"  navigation task objects alive after full GC: {TasksAlive}/{Iterations}",
			$"  payload pages alive after full GC: {PayloadPagesAlive}/{Iterations}",
			$"  payload view models alive after full GC: {PayloadViewModelsAlive}/{Iterations}",
			$"  payload byte arrays alive after full GC: {PayloadArraysAlive}/{Iterations}",
			$"  retained payload bytes: {FormatBytes(RetainedPayloadBytes)}",
			$"  managed heap after full GC: {FormatBytes(ManagedHeapBytes)}");
	}

	sealed class PayloadPage : ContentPage
	{
		public PayloadPage(int index, int payloadBytes)
		{
			Title = $"Payload {index}";
			BindingContext = new PayloadViewModel(payloadBytes);
			Content = new Label { Text = $"Payload page {index}" };
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
namespace NavigationRendererPendingTaskRetentionRepro;

public static class ReproSession
{
	public static string ResultsPath => Path.Combine(Path.GetTempPath(), "navigationrenderer-pending-task-retention-results.txt");

	public static Task<string> RunAsync(object context)
	{
		return Task.FromResult("This repro is only implemented for Mac Catalyst/iOS.");
	}
}
#endif
