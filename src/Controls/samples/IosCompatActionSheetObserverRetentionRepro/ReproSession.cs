using System.Runtime.CompilerServices;
using System.Text;
using Foundation;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Platform;
using UIKit;

namespace IosCompatActionSheetObserverRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int ButtonsPerSheet = 8;
	const int PayloadKiBPerButton = 8;
	const long PayloadBytesPerSheet = ButtonsPerSheet * PayloadKiBPerButton * 1024L;
	const int DismissWaitMs = 100;

	public static async Task<ReproReport> RunAsync()
	{
		WriteProgress("Starting compatibility iPad action-sheet observer repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running completed-result control scenario.");
		var control = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad
			? await RunScenarioAsync(completeResultBeforeDismiss: true)
			: ScenarioResult.NotRun("control: compatibility ActionSheetArguments.Result completed before native dismiss");

		WriteProgress("Running incomplete-result current scenario.");
		var leak = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad
			? await RunScenarioAsync(completeResultBeforeDismiss: false)
			: ScenarioResult.NotRun("current: compatibility native alert dismissed while result remains incomplete");

		WriteProgress("Collecting final heap and weak-reference counts.");
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

	static async Task<ScenarioResult> RunScenarioAsync(bool completeResultBeforeDismiss)
	{
		var tracked = new List<TrackedActionSheet>(Cycles);

		for (var cycle = 0; cycle < Cycles; cycle++)
		{
			WriteProgress($"{(completeResultBeforeDismiss ? "control" : "current")} cycle {cycle + 1}/{Cycles}: starting");
			tracked.Add(await CreateActionSheetCycleAsync(cycle, completeResultBeforeDismiss));

			if (cycle % 10 == 0)
				await Task.Yield();
		}

		await Task.Delay(1500);
		ForceFullGc();

		var name = completeResultBeforeDismiss
			? "control: completed compatibility ActionSheetArguments.Result before native dismiss"
			: "current: compatibility native alert dismissed while ActionSheetArguments.Result remains incomplete";

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task<TrackedActionSheet> CreateActionSheetCycleAsync(int cycle, bool completeResultBeforeDismiss)
	{
		var buttons = CreateButtons(cycle);
		var title = $"Compatibility dispatch actions {cycle:0000}";
		var arguments = new ActionSheetArguments(title, "Cancel", null, buttons);

		WriteProgress($"cycle {cycle + 1}: mirroring compatibility Platform.PresentActionSheet");
		var presented = await MainThread.InvokeOnMainThreadAsync(() => PresentCompatibilityActionSheet(arguments));
		await WaitForPresentationAsync(presented.Alert);
		var tracked = TrackedActionSheet.Create(arguments, presented.Alert, presented.Window, buttons, PayloadBytesPerSheet);

		if (completeResultBeforeDismiss)
		{
			WriteProgress($"cycle {cycle + 1}: completing ActionSheetArguments.Result");
			await MainThread.InvokeOnMainThreadAsync(() => arguments.SetResult(arguments.Cancel));
		}

		WriteProgress($"cycle {cycle + 1}: dismissing native alert");
		await DismissAlertAsync(presented.Alert);
		WriteProgress($"cycle {cycle + 1}: hiding compatibility UIWindow");
		await MainThread.InvokeOnMainThreadAsync(() => presented.Window.Hidden = true);

		if (completeResultBeforeDismiss)
			await WaitForCompletedCleanupAsync(arguments);
		else
			await Task.Delay(DismissWaitMs);

		return tracked;
	}

	static PresentedActionSheet PresentCompatibilityActionSheet(ActionSheetArguments arguments)
	{
		var alert = UIAlertController.Create(arguments.Title, null, UIAlertControllerStyle.ActionSheet);
#pragma warning disable CA1422 // Repro mirrors obsolete compatibility Platform.cs UIWindow construction.
		var window = new UIWindow { BackgroundColor = UIColor.Clear };
#pragma warning restore CA1422

		alert.AddAction(CreateActionWithWindowHide(arguments.Cancel ?? string.Empty, UIAlertActionStyle.Cancel, () => arguments.SetResult(arguments.Cancel), window));

		if (arguments.Destruction != null)
			alert.AddAction(CreateActionWithWindowHide(arguments.Destruction, UIAlertActionStyle.Destructive, () => arguments.SetResult(arguments.Destruction), window));

		foreach (var label in arguments.Buttons)
		{
			if (label == null)
				continue;

			var blabel = label;
			alert.AddAction(CreateActionWithWindowHide(blabel, UIAlertActionStyle.Default, () => arguments.SetResult(blabel), window));
		}

		PresentCompatibilityPopUp(window, alert, arguments);
		return new PresentedActionSheet(alert, window);
	}

	static UIAlertAction CreateActionWithWindowHide(string text, UIAlertActionStyle style, Action setResult, UIWindow window)
	{
		return UIAlertAction.Create(text, style, _ =>
		{
#pragma warning disable CA1422 // Repro mirrors obsolete compatibility Platform.cs UIWindow action cleanup.
			window.Hidden = true;
#pragma warning restore CA1422
			setResult();
		});
	}

	static void PresentCompatibilityPopUp(UIWindow window, UIAlertController alert, ActionSheetArguments arguments)
	{
		window.RootViewController = new UIViewController();
		window.RootViewController.View!.BackgroundColor = UIColor.Clear;
#pragma warning disable CA1422 // Repro mirrors obsolete compatibility Platform.cs UIWindow presentation.
		window.WindowLevel = UIWindowLevel.Alert + 1;
		window.MakeKeyAndVisible();
#pragma warning restore CA1422

		if (UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad)
		{
			UIDevice.CurrentDevice.BeginGeneratingDeviceOrientationNotifications();
			var observer = NSNotificationCenter.DefaultCenter.AddObserver(UIDevice.OrientationDidChangeNotification,
				_ =>
				{
					if (alert.PopoverPresentationController != null)
						alert.PopoverPresentationController.SourceRect = window.RootViewController.View.Bounds;
				});

			arguments.Result.Task.ContinueWith(_ =>
			{
				NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
				UIDevice.CurrentDevice.EndGeneratingDeviceOrientationNotifications();
			}, TaskScheduler.FromCurrentSynchronizationContext());

			if (alert.PopoverPresentationController != null)
			{
				alert.PopoverPresentationController.SourceView = window.RootViewController.View;
				alert.PopoverPresentationController.SourceRect = window.RootViewController.View.Bounds;
				alert.PopoverPresentationController.PermittedArrowDirections = 0;
			}
		}

		window.RootViewController.PresentViewController(alert, true, null);
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

	static async Task WaitForPresentationAsync(UIAlertController alert)
	{
		for (var i = 0; i < 200; i++)
		{
			if (alert.PresentingViewController != null)
				return;
			await Task.Delay(10);
		}

		throw new TimeoutException("Timed out waiting for the compatibility action sheet UIAlertController.");
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
			throw new TimeoutException("Timed out waiting for the compatibility action sheet UIAlertController to dismiss.");
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
			using var pool = new NSAutoreleasePool();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(50);
		}
	}

	static void WriteProgress(string message)
	{
		try
		{
			File.WriteAllText(AutoRunSettings.GetResultsPath(), "PROGRESS: " + message + Environment.NewLine);
		}
		catch
		{
			// Progress output is diagnostic only; the final report write remains authoritative.
		}
	}

	internal sealed record PresentedActionSheet(
		UIAlertController Alert,
		UIWindow Window);

	internal sealed record TrackedActionSheet(
		WeakReference Arguments,
		WeakReference Alert,
		WeakReference Window,
		WeakReference ButtonArray,
		long ExpectedPayloadBytes)
	{
		public static TrackedActionSheet Create(
			ActionSheetArguments arguments,
			UIAlertController alert,
			UIWindow window,
			string[] buttons,
			long expectedPayloadBytes)
		{
			return new TrackedActionSheet(
				new WeakReference(arguments),
				new WeakReference(alert),
				new WeakReference(window),
				new WeakReference(buttons),
				expectedPayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedSheets,
		int AliveArguments,
		int AliveAlerts,
		int AliveWindows,
		int AliveButtonArrays,
		long EstimatedRetainedButtonBytes)
	{
		public static ScenarioResult NotRun(string name)
		{
			return new ScenarioResult(name, 0, 0, 0, 0, 0, 0);
		}

		public static ScenarioResult From(string name, IReadOnlyList<TrackedActionSheet> sheets)
		{
			var aliveArguments = 0;
			var aliveAlerts = 0;
			var aliveWindows = 0;
			var aliveButtonArrays = 0;
			long retainedButtonBytes = 0;

			foreach (var sheet in sheets)
			{
				if (sheet.Arguments.IsAlive)
					aliveArguments++;

				if (sheet.Alert.IsAlive)
					aliveAlerts++;

				if (sheet.Window.IsAlive)
					aliveWindows++;

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
				aliveWindows,
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
				"iOS iPad compatibility Platform DisplayActionSheet orientation observer retention repro",
				$"User interface idiom: {UserInterfaceIdiom}",
				$"Cycles: {Cycles}",
				$"Buttons per action sheet: {ButtonsPerSheet}",
				$"Payload per button label: {PayloadKiBPerButton} KiB",
				$"Leak proved: {LeakProved}",
				"Source path mirrored: src/Compatibility/Core/src/iOS/Platform.cs PresentActionSheet, PresentPopUp, and CreateActionWithWindowHide.",
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
				$"  Compatibility UIWindows alive: {result.AliveWindows}/{result.TrackedSheets}",
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
