using System.Reflection;
using Foundation;
using Microsoft.Maui.Platform;
using UIKit;

namespace KeyboardAutoManagerDisconnectLeakRepro;

internal static class ReproSession
{
	const int Attempts = 12;
	const int PayloadMegabytes = 16;
	const string TextFieldDidBeginEditingNotification = "UITextFieldTextDidBeginEditingNotification";
	const string KeyboardWillHideNotification = "UIKeyboardWillHideNotification";
	const string KeyboardDidHideNotification = "UIKeyboardDidHideNotification";

	static readonly Type ManagerType = typeof(KeyboardAutoManagerScroll);

	static readonly FieldInfo? ViewField = ManagerType.GetField("View", BindingFlags.Static | BindingFlags.NonPublic);
	static readonly FieldInfo? ContainerViewField = ManagerType.GetField("ContainerView", BindingFlags.Static | BindingFlags.NonPublic);
	static readonly FieldInfo? LastScrollViewField = ManagerType.GetField("LastScrollView", BindingFlags.Static | BindingFlags.NonPublic);
	static readonly FieldInfo? ScrolledViewField = ManagerType.GetField("ScrolledView", BindingFlags.Static | BindingFlags.NonPublic);
	static readonly FieldInfo? CursorRectField = ManagerType.GetField("CursorRect", BindingFlags.Static | BindingFlags.NonPublic);
	static readonly FieldInfo? IsKeyboardAutoScrollHandlingField = ManagerType.GetField("IsKeyboardAutoScrollHandling", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
	static readonly FieldInfo? IsKeyboardShowingField = ManagerType.GetField("IsKeyboardShowing", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
	static readonly FieldInfo? KeyboardFrameField = ManagerType.GetField("KeyboardFrame", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
	static readonly FieldInfo? ShouldIgnoreSafeAreaAdjustmentField = ManagerType.GetField("ShouldIgnoreSafeAreaAdjustment", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
	static readonly FieldInfo? ShouldScrollAgainField = ManagerType.GetField("ShouldScrollAgain", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

	public static ReproReport Run()
	{
		ResetManagerState();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunNormalHideControl();
		var leak = RunDisconnectWhileEditing();

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Attempts,
			PayloadMegabytes,
			baselineBytes,
			finalBytes,
			control,
			leak);
	}

	static ScenarioResult RunNormalHideControl()
	{
		ResetManagerState();
		var tracked = new List<TrackedAttempt>();

		for (var i = 0; i < Attempts; i++)
			tracked.Add(RunNormalHideControlAttempt(i));

		ForceFullGc();
		return ScenarioResult.From("control: begin-editing followed by normal keyboard hide", tracked);
	}

	static ScenarioResult RunDisconnectWhileEditing()
	{
		ResetManagerState();
		var tracked = new List<TrackedAttempt>();

		for (var i = 0; i < Attempts; i++)
			tracked.Add(RunDisconnectWhileEditingAttempt(i));

		ForceFullGc();
		return ScenarioResult.From("leak: Disconnect while an editor is active", tracked);
	}

	static TrackedAttempt RunNormalHideControlAttempt(int attempt)
	{
		var field = CreatePayloadField(attempt);
		var tracked = TrackedAttempt.Create(attempt, field, field.Payload);

		KeyboardAutoManagerScroll.Connect();
		Post(TextFieldDidBeginEditingNotification, field);
		Post(KeyboardWillHideNotification, null);
		Post(KeyboardDidHideNotification, null);
		KeyboardAutoManagerScroll.Disconnect();

		return tracked;
	}

	static TrackedAttempt RunDisconnectWhileEditingAttempt(int attempt)
	{
		var field = CreatePayloadField(attempt);
		var tracked = TrackedAttempt.Create(attempt, field, field.Payload);

		KeyboardAutoManagerScroll.Connect();
		Post(TextFieldDidBeginEditingNotification, field);
		KeyboardAutoManagerScroll.Disconnect();

		return tracked;
	}

	static PayloadTextField CreatePayloadField(int attempt)
	{
		var payload = new EditorPayload(attempt, PayloadMegabytes * 1024L * 1024L);
		return new PayloadTextField(payload)
		{
			Text = $"Customer search filter {attempt:0000}"
		};
	}

	static void Post(string notificationName, NSObject? value)
	{
		using var name = new NSString(notificationName);
		NSNotificationCenter.DefaultCenter.PostNotificationName(name, value);
	}

	static void ResetManagerState()
	{
		KeyboardAutoManagerScroll.Disconnect();
		ViewField?.SetValue(null, null);
		ContainerViewField?.SetValue(null, null);
		LastScrollViewField?.SetValue(null, null);
		ScrolledViewField?.SetValue(null, null);
		CursorRectField?.SetValue(null, null);
		IsKeyboardAutoScrollHandlingField?.SetValue(null, false);
		IsKeyboardShowingField?.SetValue(null, false);
		KeyboardFrameField?.SetValue(null, CoreGraphics.CGRect.Empty);
		ShouldIgnoreSafeAreaAdjustmentField?.SetValue(null, false);
		ShouldScrollAgainField?.SetValue(null, false);
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 5; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	internal sealed class PayloadTextField : UITextField
	{
		public PayloadTextField(EditorPayload payload)
		{
			Payload = payload;
		}

		public EditorPayload Payload { get; }
	}

	internal sealed class EditorPayload
	{
		public EditorPayload(int attempt, long payloadBytes)
		{
			Attempt = attempt;
			PayloadBytes = payloadBytes;
			WorkspaceBytes = new byte[payloadBytes];

			for (var i = 0; i < WorkspaceBytes.Length; i += 4096)
				WorkspaceBytes[i] = (byte)(attempt + i);

			EditorState = Enumerable.Range(0, 64)
				.Select(index => $"draft-search-token-{attempt:0000}-{index:0000}")
				.ToArray();
		}

		public int Attempt { get; }

		public long PayloadBytes { get; }

		public byte[] WorkspaceBytes { get; }

		public IReadOnlyList<string> EditorState { get; }
	}

	internal sealed record TrackedAttempt(
		int Attempt,
		WeakReference TextField,
		WeakReference Payload,
		long PayloadBytes)
	{
		public static TrackedAttempt Create(int attempt, PayloadTextField textField, EditorPayload payload)
		{
			return new TrackedAttempt(
				attempt,
				new WeakReference(textField),
				new WeakReference(payload),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedAttempts,
		int AliveTextFields,
		int AlivePayloads,
		long RetainedPayloadBytes)
	{
		public static ScenarioResult From(string name, IReadOnlyList<TrackedAttempt> attempts)
		{
			var aliveTextFields = 0;
			var alivePayloads = 0;
			long retainedPayloadBytes = 0;

			foreach (var attempt in attempts)
			{
				if (attempt.TextField.IsAlive)
					aliveTextFields++;

				if (attempt.Payload.IsAlive)
				{
					alivePayloads++;
					retainedPayloadBytes += attempt.PayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				attempts.Count,
				aliveTextFields,
				alivePayloads,
				retainedPayloadBytes);
		}
	}

	internal sealed record ReproReport(
		int Attempts,
		int PayloadMegabytes,
		long BaselineManagedBytes,
		long FinalManagedBytes,
		ScenarioResult Control,
		ScenarioResult Leak)
	{
		public bool LeakProved =>
			Control.AliveTextFields == 0 &&
			Control.AlivePayloads == 0 &&
			Leak.AliveTextFields == 1 &&
			Leak.AlivePayloads == 1;

		public string ToText()
		{
			return string.Join(Environment.NewLine,
				"KeyboardAutoManagerDisconnectLeakRepro",
				$"Attempts: {Attempts}",
				$"Payload per editor: {PayloadMegabytes} MiB",
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

		string FormatScenario(ScenarioResult result)
		{
			var expectedPayload = result.TrackedAttempts == 0 ? 0 : result.TrackedAttempts * PayloadMegabytes * 1024L * 1024L;
			var retainedPercent = expectedPayload == 0 ? 0 : result.RetainedPayloadBytes * 100.0 / expectedPayload;

			return string.Join(Environment.NewLine,
				$"Run: {result.Name}",
				$"  tracked editors: {result.TrackedAttempts}",
				$"  text fields alive after full GC: {result.AliveTextFields}/{result.TrackedAttempts}",
				$"  editor payloads alive after full GC: {result.AlivePayloads}/{result.TrackedAttempts}",
				$"  retained payload bytes: {FormatBytes(result.RetainedPayloadBytes)} ({retainedPercent:0.0}%)");
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
