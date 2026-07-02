using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls.Internals;

var options = ReproOptions.Parse(args);
var probe = new RegistrarExtraAssembliesRetentionProbe(options);
var report = probe.Run();

Console.WriteLine(report);

if (!string.IsNullOrWhiteSpace(options.ResultsPath))
{
	var resultsDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ResultsPath));
	if (!string.IsNullOrEmpty(resultsDirectory))
		Directory.CreateDirectory(resultsDirectory);

	File.WriteAllText(options.ResultsPath, report.ToString());
}

return report.Proven ? 0 : 1;

sealed class RegistrarExtraAssembliesRetentionProbe
{
	readonly ReproOptions _options;

	public RegistrarExtraAssembliesRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearExtraAssembliesBeforeCollect: true);
		var current = RunScenario(clearExtraAssembliesBeforeCollect: false);

		Registrar.ExtraAssemblies = null;
		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearExtraAssembliesBeforeCollect)
	{
		Registrar.ExtraAssemblies = null;

		var assemblyRefs = new List<WeakReference<Assembly>>(_options.AssemblyCount);
		var typeRefs = new List<WeakReference<Type>>(_options.AssemblyCount);
		var payloadRefs = new List<WeakReference<byte[]>>(_options.AssemblyCount);

		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		CreateDynamicAssembliesAndRegister(assemblyRefs, typeRefs, payloadRefs);

		var extraAssembliesBeforeCollect = CountExtraAssemblies();

		if (clearExtraAssembliesBeforeCollect)
			Registrar.ExtraAssemblies = null;

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPayloads = CountAlive(payloadRefs);
		return new ScenarioResult(
			ExtraAssembliesBeforeCollect: extraAssembliesBeforeCollect,
			ExtraAssembliesAfterCollect: CountExtraAssemblies(),
			RetainedAssemblyCount: CountAlive(assemblyRefs),
			RetainedTypeCount: CountAlive(typeRefs),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	void CreateDynamicAssembliesAndRegister(
		List<WeakReference<Assembly>> assemblyRefs,
		List<WeakReference<Type>> typeRefs,
		List<WeakReference<byte[]>> payloadRefs)
	{
		var assemblies = new Assembly[_options.AssemblyCount];

		for (var i = 0; i < _options.AssemblyCount; i++)
		{
			var assemblyName = new AssemblyName($"MauiRegistrarExtraAssembliesRetentionRepro{i}");
			var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
			var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

			var typeBuilder = moduleBuilder.DefineType(
				$"TenantRendererPack{i}",
				TypeAttributes.Public | TypeAttributes.Class);
			typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);
			var payloadField = typeBuilder.DefineField(
				"Payload",
				typeof(byte[]),
				FieldAttributes.Public | FieldAttributes.Static);

			var type = typeBuilder.CreateType()!;
			var payload = new byte[_options.PayloadBytes];
			for (var offset = 0; offset < payload.Length; offset += 4096)
				payload[offset] = (byte)(i % 251);

			type.GetField(payloadField.Name)!.SetValue(null, payload);

			assemblies[i] = type.Assembly;
			assemblyRefs.Add(new WeakReference<Assembly>(type.Assembly, trackResurrection: false));
			typeRefs.Add(new WeakReference<Type>(type, trackResurrection: false));
			payloadRefs.Add(new WeakReference<byte[]>(payload, trackResurrection: false));
		}

		Registrar.ExtraAssemblies = assemblies;
	}

	static int CountExtraAssemblies()
	{
		if (Registrar.ExtraAssemblies is null)
			return 0;

		var count = 0;
		foreach (var _ in Registrar.ExtraAssemblies)
			count++;

		return count;
	}

	static void CollectHard()
	{
		for (var i = 0; i < 8; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(50);
		}
	}

	static int CountAlive<T>(IEnumerable<WeakReference<T>> references)
		where T : class
	{
		var count = 0;
		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out _))
				count++;
		}

		return count;
	}
}

sealed record ReproOptions(int AssemblyCount, int PayloadBytes, string? ResultsPath)
{
	public static ReproOptions Parse(string[] args)
	{
		var assemblyCount = 80;
		var payloadMiB = 1;
		string? resultsPath = null;

		foreach (var arg in args)
		{
			if (arg.StartsWith("--count=", StringComparison.Ordinal))
			{
				assemblyCount = int.Parse(arg["--count=".Length..]);
			}
			else if (arg.StartsWith("--payload-mib=", StringComparison.Ordinal))
			{
				payloadMiB = int.Parse(arg["--payload-mib=".Length..]);
			}
			else if (arg.StartsWith("--results=", StringComparison.Ordinal))
			{
				resultsPath = arg["--results=".Length..];
			}
		}

		if (assemblyCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(assemblyCount));
		if (payloadMiB <= 0)
			throw new ArgumentOutOfRangeException(nameof(payloadMiB));

		return new ReproOptions(assemblyCount, payloadMiB * 1024 * 1024, resultsPath);
	}
}

sealed record ScenarioResult(
	int ExtraAssembliesBeforeCollect,
	int ExtraAssembliesAfterCollect,
	int RetainedAssemblyCount,
	int RetainedTypeCount,
	int RetainedPayloadCount,
	long RetainedPayloadBytes,
	long HeapBeforeBytes,
	long HeapAfterBytes)
{
	public long HeapDeltaBytes => HeapAfterBytes - HeapBeforeBytes;
}

sealed record ReproReport(ReproOptions Options, ScenarioResult Control, ScenarioResult Current)
{
	public bool Proven =>
		Control.ExtraAssembliesAfterCollect == 0
		&& Control.RetainedAssemblyCount == 0
		&& Control.RetainedTypeCount == 0
		&& Control.RetainedPayloadCount == 0
		&& Current.ExtraAssembliesAfterCollect == Options.AssemblyCount
		&& Current.RetainedAssemblyCount == Options.AssemblyCount
		&& Current.RetainedTypeCount == Options.AssemblyCount
		&& Current.RetainedPayloadCount == Options.AssemblyCount;

	public override string ToString()
	{
		return $"""
			Registrar.ExtraAssemblies retention repro
			Result: {(Proven ? "PROVEN" : "NOT PROVEN")}

			Trigger:
			  Registrar.ExtraAssemblies is a public static IEnumerable<Assembly> used by compatibility registration scans.
			  Forms.Init(rendererAssemblies) stores rendererAssemblies.ToArray() in this static property on several compatibility platforms.
			  There is no public unregister or eviction path for plugin/module unload.

			Dynamic assemblies: {Options.AssemblyCount}
			Payload per assembly: {Options.PayloadBytes / 1024 / 1024} MiB

			Control: Registrar.ExtraAssemblies cleared before forced GC
			  ExtraAssemblies before collect: {Control.ExtraAssembliesBeforeCollect}
			  ExtraAssemblies after collect: {Control.ExtraAssembliesAfterCollect}
			  Retained assemblies: {Control.RetainedAssemblyCount}/{Options.AssemblyCount}
			  Retained types: {Control.RetainedTypeCount}/{Options.AssemblyCount}
			  Retained payloads: {Control.RetainedPayloadCount}/{Options.AssemblyCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}
			  Managed heap delta: {Control.HeapDeltaBytes:N0} bytes

			Current MAUI: Registrar.ExtraAssemblies left intact
			  ExtraAssemblies before collect: {Current.ExtraAssembliesBeforeCollect}
			  ExtraAssemblies after collect: {Current.ExtraAssembliesAfterCollect}
			  Retained assemblies: {Current.RetainedAssemblyCount}/{Options.AssemblyCount}
			  Retained types: {Current.RetainedTypeCount}/{Options.AssemblyCount}
			  Retained payloads: {Current.RetainedPayloadCount}/{Options.AssemblyCount}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			  Managed heap delta: {Current.HeapDeltaBytes:N0} bytes
			""";
	}
}
