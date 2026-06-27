using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;

namespace ShellPendingPreviousPageRetentionRepro;

public static class ReproSession
{
	const int PayloadBytes = 48 * 1024 * 1024;

	static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

	static readonly FieldInfo PreviousPageField =
		typeof(Shell).GetField("_previousPage", InstanceNonPublic)
		?? throw new MissingFieldException(nameof(Shell), "_previousPage");

	static readonly FieldInfo NavigationTypeField =
		typeof(Shell).GetField("_navigationType", InstanceNonPublic)
		?? throw new MissingFieldException(nameof(Shell), "_navigationType");

	static readonly FieldInfo PendingPreviousPageField =
		typeof(Shell).GetField("_pendingPreviousPage", InstanceNonPublic)
		?? throw new MissingFieldException(nameof(Shell), "_pendingPreviousPage");

	static readonly FieldInfo PendingNavigationTypeField =
		typeof(Shell).GetField("_pendingNavigationType", InstanceNonPublic)
		?? throw new MissingFieldException(nameof(Shell), "_pendingNavigationType");

	static readonly MethodInfo PropagateSendNavigatedToMethod =
		typeof(Shell).GetMethod("PropagateSendNavigatedTo", InstanceNonPublic)
		?? throw new MissingMethodException(nameof(Shell), "PropagateSendNavigatedTo");

	static readonly MethodInfo SendNavigatingMethod =
		typeof(Shell).GetMethod("SendNavigating", InstanceNonPublic)
		?? throw new MissingMethodException(nameof(Shell), "SendNavigating");

	public static string ResultsPath => Path.Combine(Path.GetTempPath(), "shell-pending-previous-page-retention-results.txt");

	public static string Run()
	{
		var controlState = CreateScenario(clearPendingWhenNavigatingAway: true);
		ForceFullGc();
		var control = Inspect(controlState);

		var currentState = CreateScenario(clearPendingWhenNavigatingAway: false);
		ForceFullGc();
		var current = Inspect(currentState);

		var leakProved =
			control.PayloadArraysAlive == 0 &&
			current.PayloadArraysAlive == 1 &&
			current.PendingPreviousPageAssigned;

		return string.Join(Environment.NewLine,
			"ShellPendingPreviousPageRetentionRepro",
			$"Result path: {ResultsPath}",
			$"Payload page size: {FormatBytes(PayloadBytes)}",
			$"Leak proved: {leakProved}",
			string.Empty,
			control.ToReport("control: clear _pendingPreviousPage when navigating away before Loaded"),
			string.Empty,
			current.ToReport("current: SendNavigating removes Loaded handler but leaves _pendingPreviousPage assigned"));
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioState CreateScenario(bool clearPendingWhenNavigatingAway)
	{
		var shell = new Shell();
		var deferredDestination = new ContentPage
		{
			Title = "Destination that has not fired Loaded",
			Content = new Label { Text = "Deferred destination" }
		};

		var shellContent = new ShellContent
		{
			Route = $"deferred{Guid.NewGuid():N}",
			Content = deferredDestination
		};
		var shellSection = new ShellSection();
		shellSection.Items.Add(shellContent);
		var shellItem = new FlyoutItem();
		shellItem.Items.Add(shellSection);
		shell.Items.Add(shellItem);

		if (!ReferenceEquals(shell.CurrentPage, deferredDestination))
			throw new InvalidOperationException("The repro Shell did not select the deferred destination page.");

		var previousPage = new PayloadPage(PayloadBytes);
		var previousViewModel = (PayloadViewModel)previousPage.BindingContext;
		var payload = previousViewModel.Payload;

		var pageReference = new WeakReference(previousPage);
		var viewModelReference = new WeakReference(previousViewModel);
		var payloadReference = new WeakReference(payload);

		PreviousPageField.SetValue(shell, previousPage);
		NavigationTypeField.SetValue(shell, NavigationType.Replace);

		// This is the state created by Shell.SendNavigated() when the destination page has not
		// fired Loaded yet: _pendingPreviousPage points at the old page graph until Loaded runs.
		PropagateSendNavigatedToMethod.Invoke(shell, null);

		previousPage = null!;
		previousViewModel = null!;
		payload = null!;

		var navigatingArgs = new ShellNavigatingEventArgs(
			new ShellNavigationState("//deferred"),
			new ShellNavigationState("//next"),
			ShellNavigationSource.ShellContentChanged,
			canCancel: false);

		// Simulates a second navigation before the destination page fires Loaded. Current MAUI
		// removes the Loaded handler in this path, but it does not clear _pendingPreviousPage.
		SendNavigatingMethod.Invoke(shell, new object[] { navigatingArgs });

		if (clearPendingWhenNavigatingAway)
		{
			PendingPreviousPageField.SetValue(shell, null);
			PendingNavigationTypeField.SetValue(shell, default(NavigationType));
		}

		PreviousPageField.SetValue(shell, null);

		deferredDestination = null!;
		shellContent = null!;
		shellSection = null!;
		shellItem = null!;

		return new ScenarioState(shell, pageReference, viewModelReference, payloadReference);
	}

	static ScenarioResult Inspect(ScenarioState state)
	{
		var pendingPreviousPage = PendingPreviousPageField.GetValue(state.RootedShell) as Page;

		return new ScenarioResult(
			PendingPreviousPageAssigned: pendingPreviousPage is not null,
			PayloadPagesAlive: state.PageReference.IsAlive ? 1 : 0,
			PayloadViewModelsAlive: state.ViewModelReference.IsAlive ? 1 : 0,
			PayloadArraysAlive: state.PayloadReference.IsAlive ? 1 : 0,
			RetainedPayloadBytes: state.PayloadReference.IsAlive ? PayloadBytes : 0,
			ManagedHeapBytes: GC.GetTotalMemory(forceFullCollection: true));
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

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024d / 1024d:0.0} MiB";

		if (bytes >= 1024)
			return $"{bytes / 1024d:0.0} KiB";

		return $"{bytes} B";
	}

	sealed record ScenarioState(
		Shell RootedShell,
		WeakReference PageReference,
		WeakReference ViewModelReference,
		WeakReference PayloadReference);

	sealed record ScenarioResult(
		bool PendingPreviousPageAssigned,
		int PayloadPagesAlive,
		int PayloadViewModelsAlive,
		int PayloadArraysAlive,
		long RetainedPayloadBytes,
		long ManagedHeapBytes)
	{
		public string ToReport(string name) => string.Join(Environment.NewLine,
			$"Run: {name}",
			$"  _pendingPreviousPage assigned: {PendingPreviousPageAssigned}",
			$"  payload pages alive after full GC: {PayloadPagesAlive}/1",
			$"  payload view models alive after full GC: {PayloadViewModelsAlive}/1",
			$"  payload byte arrays alive after full GC: {PayloadArraysAlive}/1",
			$"  retained payload bytes: {FormatBytes(RetainedPayloadBytes)}",
			$"  managed heap after full GC: {FormatBytes(ManagedHeapBytes)}");
	}

	sealed class PayloadPage : ContentPage
	{
		public PayloadPage(int payloadBytes)
		{
			Title = "Previous heavy page";
			BindingContext = new PayloadViewModel(payloadBytes);
			Content = new VerticalStackLayout
			{
				Children =
				{
					new Label { Text = "Previous heavy page" },
					new Label { Text = $"Payload: {FormatBytes(payloadBytes)}" }
				}
			};
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
