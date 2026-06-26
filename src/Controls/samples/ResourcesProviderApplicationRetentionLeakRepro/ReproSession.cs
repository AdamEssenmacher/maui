using System.Reflection;
using Microsoft.Maui.Controls.Internals;

namespace ResourcesProviderApplicationRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int AliveApplications,
	int AlivePayloads,
	bool ProviderDictionaryAlive,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveApplications == 0 &&
		Control.AlivePayloads == 0 &&
		Current.AliveApplications == 1 &&
		Current.AlivePayloads == 1 &&
		Current.ProviderDictionaryAlive;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"ResourcesProviderApplicationRetentionLeakRepro",
			$"Payload size: {FormatBytes(PayloadBytes)}",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  provider dictionary alive: {stats.ProviderDictionaryAlive}",
			$"  applications alive after full GC: {stats.AliveApplications}/1",
			$"  payloads alive after full GC: {stats.AlivePayloads}/1",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)}");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : "";
		var value = Math.Abs((double)bytes);
		if (value >= 1024 * 1024)
			return $"{sign}{value / 1024 / 1024:0.0} MiB";
		if (value >= 1024)
			return $"{sign}{value / 1024:0.0} KiB";
		return $"{bytes} B";
	}
}

internal static class ReproSession
{
	const int PayloadBytes = 80 * 1024 * 1024;

	static readonly PropertyInfo SystemResourcesProperty =
		typeof(Application).GetProperty("SystemResources", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(typeof(Application).FullName, "SystemResources");

	static readonly Type ResourceDictionaryType = Type.GetType("Microsoft.Maui.Controls.Compatibility.Platform.iOS.ResourcesProvider, Microsoft.Maui.Controls")
		?? throw new InvalidOperationException("iOS compatibility ResourcesProvider type was not found.");

	static readonly FieldInfo ProviderDictionaryField =
		ResourceDictionaryType.GetField("_dictionary", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(ResourceDictionaryType.FullName, "_dictionary");

	public static async Task<ReproReport> RunAsync()
	{
		await Task.Yield();

		var originalApplication = Application.Current;
		ClearProviderDictionary();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = RunScenario(
			"control: clear ResourcesProvider._dictionary after forcing system resources",
			originalApplication,
			clearProviderDictionaryAfterSetup: true);

		var current = RunScenario(
			"current: ResourcesProvider keeps latest system ResourceDictionary",
			originalApplication,
			clearProviderDictionaryAfterSetup: false);

		Application.Current = originalApplication;

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(PayloadBytes, control, current, baseline, final);
	}

	static RunStats RunScenario(string name, Application? originalApplication, bool clearProviderDictionaryAfterSetup)
	{
		ClearProviderDictionary();

		var app = new PayloadApplication(PayloadBytes);
		var appRef = new WeakReference<PayloadApplication>(app);
		var payloadRef = new WeakReference<Payload>(app.Payload);

		var resources = ForceSystemResources(app);
		var resourceRef = new WeakReference<object>(resources);

		Application.Current = originalApplication;

		if (clearProviderDictionaryAfterSetup)
			ClearProviderDictionary();

		app = null!;
		resources = null!;

		ForceFullGc();

		var aliveApplications = appRef.TryGetTarget(out _) ? 1 : 0;
		var alivePayloads = payloadRef.TryGetTarget(out _) ? 1 : 0;
		var providerDictionaryAlive = resourceRef.TryGetTarget(out _);

		return new RunStats(
			name,
			aliveApplications,
			alivePayloads,
			providerDictionaryAlive,
			(long)alivePayloads * PayloadBytes);
	}

	static object ForceSystemResources(Application app)
	{
		return SystemResourcesProperty.GetValue(app)
			?? throw new InvalidOperationException("System resources were not created.");
	}

	static void ClearProviderDictionary()
	{
		var provider = DependencyService.Get<ISystemResourcesProvider>()
			?? throw new InvalidOperationException("No system resources provider was registered.");

		if (provider.GetType() != ResourceDictionaryType)
			throw new InvalidOperationException($"Unexpected provider type: {provider.GetType().FullName}");

		ProviderDictionaryField.SetValue(provider, null);
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

	sealed class PayloadApplication : Application
	{
		public PayloadApplication(int payloadBytes)
		{
			Payload = new Payload(payloadBytes);
		}

		public Payload Payload { get; }
	}

	sealed class Payload
	{
		public Payload(int byteCount)
		{
			Bytes = new byte[byteCount];
			Bytes[0] = 17;
			Bytes[^1] = 29;
		}

		public byte[] Bytes { get; }
	}
}
