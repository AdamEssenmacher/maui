using System.Reflection;
using System.Runtime.CompilerServices;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Authentication;
using UIKit;

namespace AppleSignInAuthManagerRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 4;
	const int PayloadMegabytesPerCycle = 48;

	static readonly Assembly EssentialsAssembly = typeof(AppleSignInAuthenticator).Assembly;

	static readonly Type AppleSignInAuthenticatorType =
		typeof(AppleSignInAuthenticator);

	static readonly Type AppleSignInImplementationType =
		EssentialsAssembly.GetType("Microsoft.Maui.Authentication.AppleSignInAuthenticatorImplementation")
		?? throw new InvalidOperationException("Could not find AppleSignInAuthenticatorImplementation.");

	static readonly Type AuthManagerType =
		EssentialsAssembly.GetType("Microsoft.Maui.Authentication.AuthManager")
		?? throw new InvalidOperationException("Could not find AuthManager.");

	static readonly FieldInfo DefaultImplementationField =
		AppleSignInAuthenticatorType.GetField("defaultImplementation", BindingFlags.NonPublic | BindingFlags.Static)
		?? throw new InvalidOperationException("Could not find AppleSignInAuthenticator.defaultImplementation.");

	static readonly FieldInfo AuthManagerField =
		AppleSignInImplementationType.GetField("authManager", BindingFlags.NonPublic | BindingFlags.Instance)
		?? throw new InvalidOperationException("Could not find AppleSignInAuthenticatorImplementation.authManager.");

	static readonly FieldInfo TcsCredentialField =
		AuthManagerType.GetField("tcsCredential", BindingFlags.NonPublic | BindingFlags.Instance)
		?? throw new InvalidOperationException("Could not find AuthManager.tcsCredential.");

	static readonly FieldInfo PresentingAnchorField =
		AuthManagerType.GetField("presentingAnchor", BindingFlags.NonPublic | BindingFlags.Instance)
		?? throw new InvalidOperationException("Could not find AuthManager.presentingAnchor.");

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "applesignin-authmanager-retention-results.txt");

	public static ReproReport Run()
	{
		ClearStaticState();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunScenario("control: clear authManager after auth error", clearAuthManagerAfterCompletion: true);
		var leak = RunScenario("current: authManager remains assigned after auth error", clearAuthManagerAfterCompletion: false);

		ClearStaticState();
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			leak);
	}

	static ScenarioResult RunScenario(string name, bool clearAuthManagerAfterCompletion)
	{
		var tracking = RunScenarioCore(clearAuthManagerAfterCompletion);

		ForceFullGc();
		var defaultImplementation = DefaultImplementationField.GetValue(null);
		var assignedAuthManager = defaultImplementation is null ? null : AuthManagerField.GetValue(defaultImplementation);
		var fieldAssignedAfterGc = assignedAuthManager is not null;
		var fieldHasPresentationWindow = assignedAuthManager is not null &&
			PresentingAnchorField.GetValue(assignedAuthManager) is UIWindow;

		if (clearAuthManagerAfterCompletion)
			ClearAuthManager(defaultImplementation);

		return ScenarioResult.From(
			name,
			tracking.TrackedCycles,
			tracking.CompletedErrors,
			fieldAssignedAfterGc,
			fieldHasPresentationWindow);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioTracking RunScenarioCore(bool clearAuthManagerAfterCompletion)
	{
		var defaultImplementation = Activator.CreateInstance(AppleSignInImplementationType)
			?? throw new InvalidOperationException("Could not create AppleSignInAuthenticatorImplementation.");

		DefaultImplementationField.SetValue(null, defaultImplementation);

		var tracked = new List<TrackedCycle>();
		var completedErrors = 0;

		for (var i = 0; i < Cycles; i++)
		{
			if (CreateCompletedAuthCycle(defaultImplementation, i, tracked, clearAuthManagerAfterCompletion))
				completedErrors++;
		}

		return new ScenarioTracking(tracked, completedErrors);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static bool CreateCompletedAuthCycle(
		object defaultImplementation,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearAuthManagerAfterCompletion)
	{
		using var pool = new NSAutoreleasePool();
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var window = new PayloadWindow(cycle, payload);
		var authManager = Activator.CreateInstance(AuthManagerType, window)
			?? throw new InvalidOperationException("Could not create AuthManager.");

		AuthManagerField.SetValue(defaultImplementation, authManager);
		var completed = CompleteAuthManagerWithError(authManager, cycle);

		tracked.Add(TrackedCycle.Create(cycle, authManager, window, payload));

		if (clearAuthManagerAfterCompletion)
			ClearAuthManager(defaultImplementation);

		return completed;
	}

	static bool CompleteAuthManagerWithError(object authManager, int cycle)
	{
		var tcs = TcsCredentialField.GetValue(authManager)
			?? throw new InvalidOperationException("AuthManager did not create a credential TCS.");

		var trySetException = tcs.GetType().GetMethod("TrySetException", [typeof(Exception)])
			?? throw new InvalidOperationException("Could not find TaskCompletionSource.TrySetException.");

		return (bool)trySetException.Invoke(
			tcs,
			[new InvalidOperationException($"Synthetic Apple Sign-In cancellation/error {cycle + 1}.")])!;
	}

	static void ClearStaticState()
	{
		var defaultImplementation = DefaultImplementationField.GetValue(null);
		ClearAuthManager(defaultImplementation);
		DefaultImplementationField.SetValue(null, null);
	}

	static void ClearAuthManager(object? defaultImplementation)
	{
		if (defaultImplementation is not null)
			AuthManagerField.SetValue(defaultImplementation, null);
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
}

sealed class PayloadWindow : UIWindow
{
#pragma warning disable CA1422 // The repro intentionally creates an unattached synthetic UIWindow graph.
	public PayloadWindow(int cycle, LeakPayload payload)
		: base(new CGRect(0, 0, 640, 480))
#pragma warning restore CA1422
	{
		Cycle = cycle;
		Payload = payload;
		AccessibilityIdentifier = $"apple-signin-payload-window-{cycle + 1}";
		RootViewController = new UIViewController
		{
			Title = $"Apple Sign-In workspace {cycle + 1}"
		};
	}

	public int Cycle { get; }

	public LeakPayload Payload { get; }
}

internal sealed class LeakPayload
{
	public LeakPayload(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		SessionBytes = new byte[payloadBytes];

		for (var i = 0; i < SessionBytes.Length; i += 4096)
			SessionBytes[i] = (byte)(cycle + i);

		WorkspaceState = Enumerable.Range(1, 16)
			.Select(index => new SignInWorkspaceItem(
				$"ACCOUNT-{cycle + 1:000}-{index:000}",
				$"Federated account selection payload {index}",
				"Awaiting completion"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] SessionBytes { get; }

	public IReadOnlyList<SignInWorkspaceItem> WorkspaceState { get; }
}

internal sealed record SignInWorkspaceItem(string Id, string Description, string Status);

internal sealed record ScenarioTracking(IReadOnlyList<TrackedCycle> TrackedCycles, int CompletedErrors);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference AuthManager,
	WeakReference Window,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(
		int cycle,
		object authManager,
		PayloadWindow window,
		LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(authManager),
			new WeakReference(window),
			new WeakReference(payload),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int TrackedCycles,
	int CompletedErrors,
	bool AuthManagerFieldAssignedAfterGc,
	bool AuthManagerFieldHasPresentationWindow,
	int AliveAuthManagers,
	int AliveWindows,
	int AlivePayloads,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(
		string name,
		IReadOnlyList<TrackedCycle> cycles,
		int completedErrors,
		bool authManagerFieldAssignedAfterGc,
		bool authManagerFieldHasPresentationWindow)
	{
		var aliveAuthManagers = 0;
		var aliveWindows = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.AuthManager.IsAlive)
				aliveAuthManagers++;
			if (cycle.Window.IsAlive)
				aliveWindows++;
			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ScenarioResult(
			name,
			cycles.Count,
			completedErrors,
			authManagerFieldAssignedAfterGc,
			authManagerFieldHasPresentationWindow,
			aliveAuthManagers,
			aliveWindows,
			alivePayloads,
			retainedPayloadBytes);
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadMegabytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ScenarioResult Control,
	ScenarioResult Current)
{
	public bool Proven =>
		Control.AliveAuthManagers == 0 &&
		Control.AliveWindows == 0 &&
		Control.AlivePayloads == 0 &&
		Current.AuthManagerFieldAssignedAfterGc &&
		Current.AuthManagerFieldHasPresentationWindow &&
		Current.AliveAuthManagers > 0 &&
		Current.AliveWindows > 0 &&
		Current.AlivePayloads > 0;

	public string ToText()
	{
		return string.Join(Environment.NewLine, new[]
		{
			"Apple Sign-In AuthManager retention repro",
			$"RESULT: {(Proven ? "PROVEN" : "NOT PROVEN")}",
			$"cycles={Cycles}",
			$"payloadMegabytesPerCycle={PayloadMegabytesPerCycle}",
			$"baselineManagedBytes={BaselineManagedBytes}",
			$"finalManagedBytes={FinalManagedBytes}",
			"",
			FormatScenario(Control),
			"",
			FormatScenario(Current)
		});
	}

	static string FormatScenario(ScenarioResult result)
	{
		return string.Join(Environment.NewLine, new[]
		{
			result.Name,
			$"  completedErrors={result.CompletedErrors}/{result.TrackedCycles}",
			$"  authManagerFieldAssignedAfterGc={result.AuthManagerFieldAssignedAfterGc}",
			$"  authManagerFieldHasPresentationWindow={result.AuthManagerFieldHasPresentationWindow}",
			$"  aliveAuthManagers={result.AliveAuthManagers}/{result.TrackedCycles}",
			$"  aliveWindows={result.AliveWindows}/{result.TrackedCycles}",
			$"  alivePayloads={result.AlivePayloads}/{result.TrackedCycles}",
			$"  retainedPayloadBytes={result.RetainedPayloadBytes}",
			$"  retainedPayloadMiB={result.RetainedPayloadBytes / 1024d / 1024d:0.0}"
		});
	}
}
