#nullable enable

using System.Reflection;
using System.Runtime.CompilerServices;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;
using UIKit;

namespace MacCatalystAlertManagerDialogRetentionRepro;

internal static class ReproSession
{
	internal const int CyclesPerDialogKind = 96;
	internal const int PayloadCharsPerSlot = 64 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * sizeof(char);
	const string PayloadPrefix = "maccatalyst-alertmanager-dialog-";

	static readonly FieldInfo AlertTitleField = GetBackingField(typeof(AlertArguments), nameof(AlertArguments.Title));
	static readonly FieldInfo AlertMessageField = GetBackingField(typeof(AlertArguments), nameof(AlertArguments.Message));
	static readonly FieldInfo PromptTitleField = GetBackingField(typeof(PromptArguments), nameof(PromptArguments.Title));
	static readonly FieldInfo PromptMessageField = GetBackingField(typeof(PromptArguments), nameof(PromptArguments.Message));
	static readonly FieldInfo PromptPlaceholderField = GetBackingField(typeof(PromptArguments), nameof(PromptArguments.Placeholder));
	static readonly FieldInfo PromptInitialValueField = GetBackingField(typeof(PromptArguments), nameof(PromptArguments.InitialValue));

	static readonly List<RetainedAlert> RetainedNativeAlerts = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "maccatalyst-alertmanager-dialog-retention-results.txt");

	public static Task<ReproReport> RunAsync(Page _page, IMauiContext _mauiContext)
	{
		RetainedNativeAlerts.Clear();
		WriteProgress("Starting Mac Catalyst AlertManager dialog retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: clear AlertManager argument/native string payloads while retaining native alerts",
			clearPayloadAfterCreate: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: AlertManager dialogs leave argument/native string payloads assigned",
			clearPayloadAfterCreate: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeAlerts);

		return Task.FromResult(new ReproReport(
			CyclesPerDialogKind,
			PayloadCharsPerSlot,
			PayloadBytesPerSlot,
			baselineBytes,
			finalBytes,
			control,
			current));
	}

	static ScenarioResult RunScenario(string name, bool clearPayloadAfterCreate)
	{
		var tracked = new List<TrackedDialog>(CyclesPerDialogKind * 2);

		for (var i = 0; i < CyclesPerDialogKind; i++)
		{
			if (i % 12 == 0)
				WriteProgress($"{name}: cycle {i}/{CyclesPerDialogKind}");

			CreateAlertCycle(i, tracked, clearPayloadAfterCreate);
			CreatePromptCycle(i, tracked, clearPayloadAfterCreate);
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateAlertCycle(
		int cycle,
		List<TrackedDialog> tracked,
		bool clearPayloadAfterCreate)
	{
		var title = CreatePayload(DialogKind.Alert, cycle, "title");
		var message = CreatePayload(DialogKind.Alert, cycle, "message");
		var arguments = new AlertArguments(title, message, "OK", "Cancel");
		var payloads = new[] { title, message };

		var alert = CreateAlertManagerStyleAlert(arguments);
		var retained = new RetainedAlert(DialogKind.Alert, alert);
		RetainedNativeAlerts.Add(retained);

		arguments.SetResult(false);

		if (clearPayloadAfterCreate)
		{
			ClearAlertArguments(arguments);
			ClearNativeAlertPayload(alert);
		}

		tracked.Add(TrackedDialog.Create(DialogKind.Alert, retained, arguments, payloads));
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreatePromptCycle(
		int cycle,
		List<TrackedDialog> tracked,
		bool clearPayloadAfterCreate)
	{
		var title = CreatePayload(DialogKind.Prompt, cycle, "title");
		var message = CreatePayload(DialogKind.Prompt, cycle, "message");
		var placeholder = CreatePayload(DialogKind.Prompt, cycle, "placeholder");
		var initialValue = CreatePayload(DialogKind.Prompt, cycle, "initial");
		var arguments = new PromptArguments(
			title,
			message,
			accept: "OK",
			cancel: "Cancel",
			placeholder: placeholder,
			maxLength: PayloadCharsPerSlot + 8,
			keyboard: Keyboard.Text,
			initialValue: initialValue);
		var payloads = new[] { title, message, placeholder, initialValue };

		var alert = CreateAlertManagerStylePrompt(arguments);
		var retained = new RetainedAlert(DialogKind.Prompt, alert);
		RetainedNativeAlerts.Add(retained);

		arguments.SetResult(null!);

		if (clearPayloadAfterCreate)
		{
			ClearPromptArguments(arguments);
			ClearNativeAlertPayload(alert);
		}

		tracked.Add(TrackedDialog.Create(DialogKind.Prompt, retained, arguments, payloads));
	}

	static UIAlertController CreateAlertManagerStyleAlert(AlertArguments arguments)
	{
		var alert = UIAlertController.Create(arguments.Title, arguments.Message, UIAlertControllerStyle.Alert);

		if (arguments.Cancel != null)
		{
			alert.AddAction(UIAlertAction.Create(arguments.Cancel, UIAlertActionStyle.Cancel,
				_ => arguments.SetResult(false)));
		}

		if (arguments.Accept != null)
		{
			alert.AddAction(UIAlertAction.Create(arguments.Accept, UIAlertActionStyle.Default,
				_ => arguments.SetResult(true)));
		}

		return alert;
	}

	static UIAlertController CreateAlertManagerStylePrompt(PromptArguments arguments)
	{
		var alert = UIAlertController.Create(arguments.Title, arguments.Message, UIAlertControllerStyle.Alert);
		alert.AddTextField(uiTextField =>
		{
			uiTextField.Placeholder = arguments.Placeholder;
			uiTextField.Text = arguments.InitialValue;
			if (arguments.MaxLength > -1 &&
				(OperatingSystem.IsIOSVersionAtLeast(26) || OperatingSystem.IsMacCatalystVersionAtLeast(26)))
			{
				uiTextField.ShouldChangeCharactersInRanges = (textField, ranges, replacementString) =>
				{
					var currentLength = textField.Text?.Length ?? 0;
					var totalRangeLength = 0;
					for (int i = 0; i < ranges.Length; i++)
					{
						var range = ranges[i].RangeValue;
						totalRangeLength += (int)range.Length;
					}

					var newLength = currentLength - totalRangeLength + replacementString.Length;
					return newLength <= arguments.MaxLength;
				};
			}
			else
			{
				uiTextField.ShouldChangeCharacters = (field, range, replacementString) =>
					arguments.MaxLength <= -1 || (field.Text?.Length ?? 0) + replacementString.Length - range.Length <= arguments.MaxLength;
			}

			uiTextField.KeyboardType = UIKeyboardType.Default;
		});

		alert.AddAction(UIAlertAction.Create(arguments.Cancel, UIAlertActionStyle.Cancel, _ => arguments.SetResult(null)));
		alert.AddAction(UIAlertAction.Create(arguments.Accept, UIAlertActionStyle.Default, _ => arguments.SetResult(alert.TextFields[0].Text)));

		return alert;
	}

	static void ClearAlertArguments(AlertArguments arguments)
	{
		AlertTitleField.SetValue(arguments, string.Empty);
		AlertMessageField.SetValue(arguments, string.Empty);
	}

	static void ClearPromptArguments(PromptArguments arguments)
	{
		PromptTitleField.SetValue(arguments, string.Empty);
		PromptMessageField.SetValue(arguments, string.Empty);
		PromptPlaceholderField.SetValue(arguments, string.Empty);
		PromptInitialValueField.SetValue(arguments, string.Empty);
	}

	static void ClearNativeAlertPayload(UIAlertController alert)
	{
		alert.Title = string.Empty;
		alert.Message = string.Empty;

		if (alert.TextFields is { } textFields)
		{
			foreach (var field in textFields)
			{
				field.Text = string.Empty;
				field.Placeholder = string.Empty;
				field.ShouldChangeCharacters = null;
				if (OperatingSystem.IsIOSVersionAtLeast(26) || OperatingSystem.IsMacCatalystVersionAtLeast(26))
					field.ShouldChangeCharactersInRanges = null;
			}
		}

		if (alert.View is { } view)
			ClearTextDescendants(view);
	}

	static void ClearTextDescendants(UIView view)
	{
		if (view is UILabel label)
		{
			label.Text = string.Empty;
			label.AttributedText = new NSAttributedString(string.Empty);
		}

		if (view is UITextField textField)
		{
			textField.Text = string.Empty;
			textField.Placeholder = string.Empty;
			textField.ShouldChangeCharacters = null;
			if (OperatingSystem.IsIOSVersionAtLeast(26) || OperatingSystem.IsMacCatalystVersionAtLeast(26))
				textField.ShouldChangeCharactersInRanges = null;
		}

		foreach (var child in view.Subviews)
			ClearTextDescendants(child);
	}

	static FieldInfo GetBackingField(Type type, string propertyName) =>
		type.GetField($"<{propertyName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException($"Could not find backing field for {type.Name}.{propertyName}.");

	static string CreatePayload(DialogKind kind, int cycle, string slot)
	{
		var prefix = $"{PayloadPrefix}{kind.ToString().ToLowerInvariant()}-{slot}-{cycle:D4}-";
		return prefix + new string((char)('A' + cycle % 26), PayloadCharsPerSlot - prefix.Length);
	}

	static bool IsPayloadString(string value) =>
		value.StartsWith(PayloadPrefix, StringComparison.Ordinal) &&
		value.Length >= PayloadCharsPerSlot;

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(100);
		}
	}

	static void WriteProgress(string message)
	{
		try
		{
			File.WriteAllText(ResultsPath, message + Environment.NewLine);
		}
		catch
		{
			// Progress output is diagnostic only; the final report write remains authoritative.
		}
	}

	internal enum DialogKind
	{
		Alert,
		Prompt
	}

	internal sealed record RetainedAlert(DialogKind Kind, UIAlertController Alert)
	{
		public int ActionCount => Alert.Actions.Length;
	}

	internal sealed record TrackedDialog(
		DialogKind Kind,
		RetainedAlert Retained,
		WeakReference<object> Arguments,
		IReadOnlyList<WeakReference<string>> PayloadStrings)
	{
		public static TrackedDialog Create(
			DialogKind kind,
			RetainedAlert retained,
			object arguments,
			IReadOnlyList<string> payloadStrings)
		{
			return new TrackedDialog(
				kind,
				retained,
				new WeakReference<object>(arguments),
				payloadStrings.Select(value => new WeakReference<string>(value)).ToArray());
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedDialogs,
		int AliveNativeAlerts,
		int RetainedNativeActions,
		int AlertDialogs,
		int PromptDialogs,
		int AliveArguments,
		int AlivePayloadStrings,
		long EstimatedAlivePayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedDialog> tracked)
		{
			var aliveNativeAlerts = 0;
			var retainedNativeActions = 0;
			var alertDialogs = 0;
			var promptDialogs = 0;
			var aliveArguments = 0;
			var alivePayloadStrings = 0;
			long estimatedAlivePayloadBytes = 0;

			foreach (var item in tracked)
			{
				if (item.Retained.Alert.Handle != IntPtr.Zero)
				{
					aliveNativeAlerts++;
					retainedNativeActions += item.Retained.ActionCount;

					if (item.Kind == DialogKind.Alert)
						alertDialogs++;
					else if (item.Kind == DialogKind.Prompt)
						promptDialogs++;
				}

				if (item.Arguments.TryGetTarget(out _))
					aliveArguments++;

				foreach (var reference in item.PayloadStrings)
				{
					if (reference.TryGetTarget(out var value) && IsPayloadString(value))
					{
						alivePayloadStrings++;
						estimatedAlivePayloadBytes += (long)value.Length * sizeof(char);
					}
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeAlerts,
				retainedNativeActions,
				alertDialogs,
				promptDialogs,
				aliveArguments,
				alivePayloadStrings,
				estimatedAlivePayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int CyclesPerDialogKind,
	int PayloadCharsPerSlot,
	int PayloadBytesPerSlot,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public int ExpectedDialogs => CyclesPerDialogKind * 2;

	public int ExpectedPayloadStrings => CyclesPerDialogKind * (2 + 4);

	public long ExpectedPayloadBytes => (long)ExpectedPayloadStrings * PayloadBytesPerSlot;

	public int ExpectedNativeActions => CyclesPerDialogKind * 4;

	public bool LeakProved =>
		Control.AliveNativeAlerts == ExpectedDialogs &&
		Current.AliveNativeAlerts == ExpectedDialogs &&
		Control.RetainedNativeActions == ExpectedNativeActions &&
		Current.RetainedNativeActions == ExpectedNativeActions &&
		Control.AlivePayloadStrings <= 1 &&
		Current.AliveArguments >= ExpectedDialogs &&
		Current.AlivePayloadStrings >= ExpectedPayloadStrings &&
		Current.EstimatedAlivePayloadBytes >= ExpectedPayloadBytes;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"MacCatalystAlertManagerDialogRetentionRepro",
			$"Cycles per dialog kind: {CyclesPerDialogKind}",
			$"Payload chars per alert/prompt string slot: {PayloadCharsPerSlot:N0}",
			$"Payload bytes per alert/prompt string slot: {PayloadBytesPerSlot:N0}",
			$"Expected retained native alert peers: {ExpectedDialogs}",
			$"Expected retained native actions: {ExpectedNativeActions}",
			$"Expected payload strings: {ExpectedPayloadStrings}",
			$"Expected payload bytes: {ExpectedPayloadBytes:N0}",
			"Source paths mirrored: AlertManager.AlertRequestHelper PresentAlert and PresentPrompt UIAlertController/UIAlertAction construction.",
			"Control keeps the native alert/action peers alive but clears payload fields from arguments and native text controls after construction.",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained payload: {FormatBytes(Control.EstimatedAlivePayloadBytes)}",
			$"Current estimated retained payload: {FormatBytes(Current.EstimatedAlivePayloadBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked dialogs: {result.TrackedDialogs}",
			$"  alive native UIAlertController peers: {result.AliveNativeAlerts}/{result.TrackedDialogs}",
			$"  retained native UIAlertActions: {result.RetainedNativeActions}",
			$"  alive alert dialogs: {result.AlertDialogs}",
			$"  alive prompt dialogs: {result.PromptDialogs}",
			$"  alive AlertArguments/PromptArguments: {result.AliveArguments}/{result.TrackedDialogs}",
			$"  alive payload strings: {result.AlivePayloadStrings}",
			$"  estimated alive payload bytes: {result.EstimatedAlivePayloadBytes:N0}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024d / 1024d:N1} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024d:N1} KiB";
		return $"{bytes:N0} B";
	}
}
