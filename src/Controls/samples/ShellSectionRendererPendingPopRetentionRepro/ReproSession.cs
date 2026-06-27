#if MACCATALYST || IOS
using System.Reflection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Platform;

namespace ShellSectionRendererPendingPopRetentionRepro;

public static class ReproSession
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly MethodInfo SendPoppedOnCompletionMethod =
		typeof(ShellSectionRenderer).GetMethod("SendPoppedOnCompletion", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(nameof(ShellSectionRenderer), "SendPoppedOnCompletion");

	static readonly FieldInfo PopCompletionTaskField =
		typeof(ShellSectionRenderer).GetField("_popCompletionTask", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(ShellSectionRenderer), "_popCompletionTask");

	static readonly FieldInfo NavStackField =
		typeof(ShellSection).GetField("_navStack", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(ShellSection), "_navStack");

	public static string ResultsPath => Path.Combine(Path.GetTempPath(), "shellsectionrenderer-pending-pop-retention-results.txt");

	public static async Task<string> RunAsync(IMauiContext context)
	{
		var control = await RunScenarioAsync(context, completePendingBeforeDispose: true);
		var current = await RunScenarioAsync(context, completePendingBeforeDispose: false);
		var leakProved =
			control.PayloadArraysAlive == 0 &&
			current.PayloadArraysAlive == Iterations &&
			current.IncompletePopTasks == Iterations;

		return string.Join(Environment.NewLine,
			"ShellSectionRendererPendingPopRetentionRepro",
			$"Result path: {ResultsPath}",
			$"Iterations: {Iterations}",
			$"Payload per pending popped page: {FormatBytes(PayloadBytes)}",
			$"Leak proved: {leakProved}",
			string.Empty,
			control.ToReport("control: complete and clear _popCompletionTask before Dispose()"),
			string.Empty,
			current.ToReport("current: Dispose() leaves _popCompletionTask unresolved"));
	}

	static async Task<ScenarioResult> RunScenarioAsync(IMauiContext context, bool completePendingBeforeDispose)
	{
		var rootedRenderers = new List<ShellSectionRenderer>(Iterations);
		var payloadPageRefs = new List<WeakReference>(Iterations);
		var payloadVmRefs = new List<WeakReference>(Iterations);
		var payloadArrayRefs = new List<WeakReference>(Iterations);
		var popTaskRefs = new List<WeakReference>(Iterations);
		var stackEntriesAfterPop = 0;

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

			var payloadPage = new PayloadPage(i, PayloadBytes);
			var payloadVm = (PayloadViewModel)payloadPage.BindingContext;
			var payloadArray = payloadVm.Payload;

			NavStackField.SetValue(shellSection, new List<Page> { null!, payloadPage });

			var popCompletion = new TaskCompletionSource<bool>();
			PopCompletionTaskField.SetValue(sectionRenderer, popCompletion);
			SendPoppedOnCompletionMethod.Invoke(sectionRenderer, new object[] { popCompletion.Task });
			await Task.Yield();

			if (shellSection.Stack.Contains(payloadPage))
				stackEntriesAfterPop++;

			if (completePendingBeforeDispose)
			{
				popCompletion.TrySetResult(false);
				PopCompletionTaskField.SetValue(sectionRenderer, null);
				await Task.Delay(50);
			}

			sectionRenderer.Dispose();

			rootedRenderers.Add(sectionRenderer);
			payloadPageRefs.Add(new WeakReference(payloadPage));
			payloadVmRefs.Add(new WeakReference(payloadVm));
			payloadArrayRefs.Add(new WeakReference(payloadArray));
			popTaskRefs.Add(new WeakReference(popCompletion.Task));

			rootPage = null!;
			shellContent = null!;
			shellSection = null!;
			shellItem = null!;
			shell = null!;
			shellRenderer = null!;
			sectionRenderer = null!;
			payloadPage = null!;
			payloadVm = null!;
			payloadArray = null!;
			popCompletion = null!;
		}

		await Task.Delay(250);
		ForceFullGc();
		await Task.Delay(250);
		ForceFullGc();

		var incompletePopTasks = 0;
		var assignedPopTasks = 0;
		foreach (var renderer in rootedRenderers)
		{
			if (PopCompletionTaskField.GetValue(renderer) is TaskCompletionSource<bool> source)
			{
				assignedPopTasks++;
				if (!source.Task.IsCompleted)
					incompletePopTasks++;
			}
		}

		return new ScenarioResult(
			assignedPopTasks,
			incompletePopTasks,
			stackEntriesAfterPop,
			CountAlive(popTaskRefs),
			CountAlive(payloadPageRefs),
			CountAlive(payloadVmRefs),
			CountAlive(payloadArrayRefs),
			(long)CountAlive(payloadArrayRefs) * PayloadBytes,
			GC.GetTotalMemory(forceFullCollection: true));
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
		int AssignedPopTasks,
		int IncompletePopTasks,
		int PayloadStackEntriesAfterPop,
		int PopTasksAlive,
		int PayloadPagesAlive,
		int PayloadViewModelsAlive,
		int PayloadArraysAlive,
		long RetainedPayloadBytes,
		long ManagedHeapBytes)
	{
		public string ToReport(string name) => string.Join(Environment.NewLine,
			$"Run: {name}",
			$"  _popCompletionTask fields still assigned: {AssignedPopTasks}/{Iterations}",
			$"  incomplete _popCompletionTask tasks: {IncompletePopTasks}/{Iterations}",
			$"  payload pages still in ShellSection.Stack after SendPopping: {PayloadStackEntriesAfterPop}/{Iterations}",
			$"  pop task objects alive after full GC: {PopTasksAlive}/{Iterations}",
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
			Title = $"Popped payload {index}";
			BindingContext = new PayloadViewModel(payloadBytes);
			Content = new Label { Text = $"Popped payload page {index}" };
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
namespace ShellSectionRendererPendingPopRetentionRepro;

public static class ReproSession
{
	public static string ResultsPath => Path.Combine(Path.GetTempPath(), "shellsectionrenderer-pending-pop-retention-results.txt");

	public static Task<string> RunAsync(object context)
	{
		return Task.FromResult("This repro is only implemented for Mac Catalyst/iOS.");
	}
}
#endif
