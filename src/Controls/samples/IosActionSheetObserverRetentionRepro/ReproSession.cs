using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using CoreGraphics;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Internals;
using UIKit;

namespace IosActionSheetObserverRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int ButtonsPerSheet = 8;
	const int PayloadKiBPerButton = 8;
	const long PayloadBytesPerSheet = ButtonsPerSheet * PayloadKiBPerButton * 1024L;
	const int DismissWaitMs = 100;

	static readonly PropertyInfo AlertManagerProperty =
		typeof(Window).GetProperty("AlertManager", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(typeof(Window).FullName, "AlertManager");

	public static async Task<ReproReport> RunAsync(Page page)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad
			? await RunScenarioAsync(page, completeResultBeforeDismiss: true)
			: ScenarioResult.NotRun("control: iPad action-sheet observer cleanup after completed result");

		var leak = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad
			? await RunScenarioAsync(page, completeResultBeforeDismiss: false)
			: ScenarioResult.NotRun("current: iPad action-sheet observer after native dismissal without MAUI result");

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			UIDevice.CurrentDevice.UserInterfaceIdiom.ToString(),
			Cycles,
			ButtonsPerSheet,
			PayloadKiBPerButton,
			baselineBytes,
			finalBytes,
			control,
			leak);
	}

	static async Task<ScenarioResult> RunScenarioAsync(Page page, bool completeResultBeforeDismiss)
	{
		var tracked = new List<TrackedActionSheet>(Cycles);

		for (var cycle = 0; cycle < Cycles; cycle++)
		{
			tracked.Add(await CreateActionSheetCycleAsync(page, cycle, completeResultBeforeDismiss));

			if (cycle % 10 == 0)
				await Task.Yield();
		}

		await Task.Delay(1500);
		ForceFullGc();

		var name = completeResultBeforeDismiss
			? "control: completed ActionSheetArguments.Result before native dismiss"
			: "current: native alert dismissed while ActionSheetArguments.Result remains incomplete";

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task<TrackedActionSheet> CreateActionSheetCycleAsync(Page page, int cycle, bool completeResultBeforeDismiss)
	{
		var buttons = CreateButtons(cycle);
		var arguments = new ActionSheetArguments($"Dispatch actions {cycle:0000}", "Cancel", null, buttons);
		var alertManager = AlertManagerProperty.GetValue(page.Window)
			?? throw new InvalidOperationException("No alert manager is available.");

		var requestActionSheet = alertManager.GetType().GetMethod(
			"RequestActionSheet",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new MissingMethodException(alertManager.GetType().FullName, "RequestActionSheet");

		await MainThread.InvokeOnMainThreadAsync(() =>
			requestActionSheet.Invoke(alertManager, new object[] { page, arguments }));

		var alert = await WaitForPresentedActionSheetAsync(page);
		var tracked = TrackedActionSheet.Create(arguments, alert, buttons, PayloadBytesPerSheet);

		if (completeResultBeforeDismiss)
			arguments.SetResult(arguments.Cancel);

		await DismissAlertAsync(alert);

		if (completeResultBeforeDismiss)
			await WaitForCompletedCleanupAsync(arguments);
		else
			await Task.Delay(DismissWaitMs);

		return tracked;
	}

	static string[] CreateButtons(int cycle)
	{
		var buttons = new string[ButtonsPerSheet];
		for (var i = 0; i < buttons.Length; i++)
			buttons[i] = CreatePayloadButton(cycle, i);

		return buttons;
	}

	static string CreatePayloadButton(int cycle, int index)
	{
		var targetChars = PayloadKiBPerButton * 1024 / 2;
		var sentence = $"Route {cycle:0000}-{index:00}: imported field-service action with customer address, SLA notes, package ids, exception codes, and audit trail text. ";
		var builder = new StringBuilder(targetChars + 64);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static async Task<UIAlertController> WaitForPresentedActionSheetAsync(Page page)
	{
		var platformWindow = page.Window?.Handler?.PlatformView as UIWindow
			?? throw new InvalidOperationException("No platform UIWindow is available.");

		for (var i = 0; i < 200; i++)
		{
			if (FindPresentedActionSheet(platformWindow.RootViewController) is { } alert)
				return alert;

			await Task.Delay(10);
		}

		throw new TimeoutException("Timed out waiting for the action sheet UIAlertController.");
	}

	static UIAlertController? FindPresentedActionSheet(UIViewController? controller)
	{
		while (controller is not null)
		{
			if (controller is UIAlertController alert &&
				alert.PreferredStyle == UIAlertControllerStyle.ActionSheet)
				return alert;

			controller = controller.PresentedViewController;
		}

		return null;
	}

	static async Task DismissAlertAsync(UIAlertController alert)
	{
		var source = new TaskCompletionSource();

		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			var controller = alert.PresentingViewController ?? alert;
			controller.DismissViewController(false, () => source.TrySetResult());
		});

		var completed = await Task.WhenAny(source.Task, Task.Delay(2000));
		if (!ReferenceEquals(completed, source.Task))
			throw new TimeoutException("Timed out waiting for the action sheet UIAlertController to dismiss.");
	}

	static async Task WaitForCompletedCleanupAsync(ActionSheetArguments arguments)
	{
		await arguments.Result.Task;
		await Task.Delay(DismissWaitMs);
	}

	static long EstimateButtonBytes(string[]? buttons)
	{
		if (buttons is null)
			return 0;

		long bytes = 0;
		foreach (var button in buttons)
			bytes += button.Length * 2L;

		return bytes;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	internal sealed record TrackedActionSheet(
		WeakReference Arguments,
		WeakReference Alert,
		WeakReference ButtonArray,
		long ExpectedPayloadBytes)
	{
		public static TrackedActionSheet Create(ActionSheetArguments arguments, UIAlertController alert, string[] buttons, long expectedPayloadBytes)
		{
			return new TrackedActionSheet(
				new WeakReference(arguments),
				new WeakReference(alert),
				new WeakReference(buttons),
				expectedPayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedSheets,
		int AliveArguments,
		int AliveAlerts,
		int AliveButtonArrays,
		long EstimatedRetainedButtonBytes)
	{
		public static ScenarioResult NotRun(string name)
		{
			return new ScenarioResult(name, 0, 0, 0, 0, 0);
		}

		public static ScenarioResult From(string name, IReadOnlyList<TrackedActionSheet> sheets)
		{
			var aliveArguments = 0;
			var aliveAlerts = 0;
			var aliveButtonArrays = 0;
			long retainedButtonBytes = 0;

			foreach (var sheet in sheets)
			{
				if (sheet.Arguments.IsAlive)
					aliveArguments++;

				if (sheet.Alert.IsAlive)
					aliveAlerts++;

				if (sheet.ButtonArray.Target is string[] buttons)
				{
					aliveButtonArrays++;
					retainedButtonBytes += Math.Min(EstimateButtonBytes(buttons), sheet.ExpectedPayloadBytes);
				}
			}

			return new ScenarioResult(
				name,
				sheets.Count,
				aliveArguments,
				aliveAlerts,
				aliveButtonArrays,
				retainedButtonBytes);
		}
	}

	internal sealed record ReproReport(
		string UserInterfaceIdiom,
		int Cycles,
		int ButtonsPerSheet,
		int PayloadKiBPerButton,
		long BaselineManagedBytes,
		long FinalManagedBytes,
		ScenarioResult Control,
		ScenarioResult Leak)
	{
		public bool LeakProved
		{
			get
			{
				var controlResidueTolerance = Math.Max(1, Cycles / 20);
				var expectedPayloadBytes = Cycles * ButtonsPerSheet * PayloadKiBPerButton * 1024L;

				return string.Equals(UserInterfaceIdiom, "Pad", StringComparison.Ordinal) &&
					Control.TrackedSheets == Cycles &&
					Control.AliveArguments <= controlResidueTolerance &&
					Control.AliveAlerts <= controlResidueTolerance &&
					Control.AliveButtonArrays <= controlResidueTolerance &&
					Leak.TrackedSheets == Cycles &&
					Leak.AliveArguments == Cycles &&
					Leak.AliveAlerts == Cycles &&
					Leak.AliveButtonArrays == Cycles &&
					Leak.EstimatedRetainedButtonBytes >= expectedPayloadBytes * 0.95;
			}
		}

		public string ToText()
		{
			return string.Join(Environment.NewLine,
				"RESULT: " + (LeakProved ? "PROVEN" : "NOT PROVEN"),
				"iOS iPad DisplayActionSheet orientation observer retention repro",
				$"User interface idiom: {UserInterfaceIdiom}",
				$"Cycles: {Cycles}",
				$"Buttons per action sheet: {ButtonsPerSheet}",
				$"Payload per button label: {PayloadKiBPerButton} KiB",
				$"Leak proved: {LeakProved}",
				string.Empty,
				FormatScenario(Control),
				string.Empty,
				FormatScenario(Leak),
				string.Empty,
				$"Managed heap baseline: {FormatBytes(BaselineManagedBytes)}",
				$"Managed heap final: {FormatBytes(FinalManagedBytes)}",
				$"Managed heap delta: {FormatBytes(FinalManagedBytes - BaselineManagedBytes)}");
		}

		static string FormatScenario(ScenarioResult result)
		{
			return string.Join(Environment.NewLine,
				$"Scenario: {result.Name}",
				$"  Tracked action sheets: {result.TrackedSheets}",
				$"  ActionSheetArguments alive: {result.AliveArguments}/{result.TrackedSheets}",
				$"  UIAlertControllers alive: {result.AliveAlerts}/{result.TrackedSheets}",
				$"  Button arrays alive: {result.AliveButtonArrays}/{result.TrackedSheets}",
				$"  Estimated retained button-label payload: {FormatBytes(result.EstimatedRetainedButtonBytes)}");
		}

		static string FormatBytes(long bytes)
		{
			var sign = bytes < 0 ? "-" : string.Empty;
			var value = Math.Abs(bytes);

			if (value >= 1024L * 1024L * 1024L)
				return $"{sign}{value / 1024d / 1024d / 1024d:0.0} GiB";

			if (value >= 1024L * 1024L)
				return $"{sign}{value / 1024d / 1024d:0.0} MiB";

			if (value >= 1024L)
				return $"{sign}{value / 1024d:0.0} KiB";

			return $"{sign}{value} B";
		}
	}
}
