using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Animations;

var options = ReproOptions.Parse(args);
var probe = new LerpCustomTypeRetentionProbe(options);
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

sealed class LerpCustomTypeRetentionProbe
{
	readonly ReproOptions _options;

	public LerpCustomTypeRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var initialBuiltInLerpCount = Lerp.Lerps.Count;
		var control = RunScenario(clearCustomLerpsBeforeCollect: true);
		var current = RunScenario(clearCustomLerpsBeforeCollect: false);

		ClearCustomLerps();
		return new ReproReport(_options, initialBuiltInLerpCount, control, current);
	}

	ScenarioResult RunScenario(bool clearCustomLerpsBeforeCollect)
	{
		ClearCustomLerps();

		var assemblyRefs = new List<WeakReference<Assembly>>(_options.TypeCount);
		var typeRefs = new List<WeakReference<Type>>(_options.TypeCount);
		var payloadRefs = new List<WeakReference<byte[]>>(_options.TypeCount);

		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		CreateDynamicTypesAndRegisterLerps(
			clearCustomLerpsBeforeCollect,
			assemblyRefs,
			typeRefs,
			payloadRefs,
			out var totalLerpsBeforeCollect,
			out var customLerpsBeforeCollect);

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPayloads = CountAlive(payloadRefs);
		return new ScenarioResult(
			TotalLerpCount: Lerp.Lerps.Count,
			CustomLerpCount: CountCustomLerps(),
			TotalLerpsBeforeCollect: totalLerpsBeforeCollect,
			CustomLerpsBeforeCollect: customLerpsBeforeCollect,
			RetainedAssemblyCount: CountAlive(assemblyRefs),
			RetainedTypeCount: CountAlive(typeRefs),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	void CreateDynamicTypesAndRegisterLerps(
		bool clearCustomLerpsBeforeCollect,
		List<WeakReference<Assembly>> assemblyRefs,
		List<WeakReference<Type>> typeRefs,
		List<WeakReference<byte[]>> payloadRefs,
		out int totalLerpsBeforeCollect,
		out int customLerpsBeforeCollect)
	{
		var customTypes = new List<Type>(_options.TypeCount);
		for (var index = 0; index < _options.TypeCount; index++)
		{
			var assemblyName = new AssemblyName($"MauiLerpCustomTypeRetentionRepro{index}");
			var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
			var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

			var typeBuilder = moduleBuilder.DefineType(
				$"TenantAnimationValue{index}",
				TypeAttributes.Public | TypeAttributes.Class);
			typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);

			var customType = typeBuilder.CreateType()!;
			var payload = new byte[_options.PayloadBytes];
			for (var offset = 0; offset < payload.Length; offset += 4096)
				payload[offset] = (byte)(index % 251);

			Lerp.Lerps[customType] = new Lerp
			{
				Calculate = (_, _, progress) =>
				{
					payload[0] = (byte)((payload[0] + (int)(progress * 10)) % 251);
					return payload[0];
				}
			};

			assemblyRefs.Add(new WeakReference<Assembly>(customType.Assembly, trackResurrection: false));
			typeRefs.Add(new WeakReference<Type>(customType, trackResurrection: false));
			payloadRefs.Add(new WeakReference<byte[]>(payload, trackResurrection: false));
			customTypes.Add(customType);
		}

		totalLerpsBeforeCollect = Lerp.Lerps.Count;
		customLerpsBeforeCollect = CountCustomLerps();

		if (clearCustomLerpsBeforeCollect)
			ClearCustomLerps(customTypes);
	}

	static void ClearCustomLerps()
	{
		var customTypes = Lerp.Lerps.Keys
			.Where(static type => type.Assembly.IsDynamic && type.FullName?.StartsWith("TenantAnimationValue", StringComparison.Ordinal) == true)
			.ToArray();

		ClearCustomLerps(customTypes);
	}

	static void ClearCustomLerps(IEnumerable<Type> customTypes)
	{
		foreach (var customType in customTypes)
			Lerp.Lerps.Remove(customType);
	}

	static int CountCustomLerps()
	{
		var count = 0;
		foreach (var customType in Lerp.Lerps.Keys)
		{
			if (customType.Assembly.IsDynamic && customType.FullName?.StartsWith("TenantAnimationValue", StringComparison.Ordinal) == true)
				count++;
		}

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

sealed record ReproOptions(int TypeCount, int PayloadBytes, string? ResultsPath)
{
	public static ReproOptions Parse(string[] args)
	{
		var typeCount = 80;
		var payloadMiB = 1;
		string? resultsPath = null;

		foreach (var arg in args)
		{
			if (arg.StartsWith("--count=", StringComparison.Ordinal))
			{
				typeCount = int.Parse(arg["--count=".Length..]);
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

		if (typeCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(typeCount));
		if (payloadMiB <= 0)
			throw new ArgumentOutOfRangeException(nameof(payloadMiB));

		return new ReproOptions(typeCount, payloadMiB * 1024 * 1024, resultsPath);
	}
}

sealed record ScenarioResult(
	int TotalLerpCount,
	int CustomLerpCount,
	int TotalLerpsBeforeCollect,
	int CustomLerpsBeforeCollect,
	int RetainedAssemblyCount,
	int RetainedTypeCount,
	int RetainedPayloadCount,
	long RetainedPayloadBytes,
	long HeapBeforeBytes,
	long HeapAfterBytes)
{
	public long HeapDeltaBytes => HeapAfterBytes - HeapBeforeBytes;
}

sealed record ReproReport(ReproOptions Options, int InitialBuiltInLerpCount, ScenarioResult Control, ScenarioResult Current)
{
	public bool Proven =>
		Control.CustomLerpCount == 0
		&& Control.TotalLerpCount == InitialBuiltInLerpCount
		&& Control.RetainedAssemblyCount == 0
		&& Control.RetainedTypeCount == 0
		&& Control.RetainedPayloadCount == 0
		&& Current.CustomLerpCount == Options.TypeCount
		&& Current.RetainedAssemblyCount == Options.TypeCount
		&& Current.RetainedTypeCount == Options.TypeCount
		&& Current.RetainedPayloadCount == Options.TypeCount;

	public override string ToString()
	{
		return $"""
			Lerp.Lerps custom type retention repro
			Result: {(Proven ? "PROVEN" : "NOT PROVEN")}

			Trigger:
			  Lerp.Lerps is a public mutable static Dictionary<Type, Lerp> used by MAUI animations.
			  Plugin or design hosts can add custom animation value types and delegates.
			  There is no scoped registration or eviction API for unloadable plugin/module types.

			Built-in lerps before repro: {InitialBuiltInLerpCount}
			Dynamic custom lerp types: {Options.TypeCount}
			Payload per custom lerp delegate: {Options.PayloadBytes / 1024 / 1024} MiB

			Control: custom Lerp.Lerps entries cleared before forced GC
			  Total lerps before collect: {Control.TotalLerpsBeforeCollect}
			  Custom lerps before collect: {Control.CustomLerpsBeforeCollect}
			  Total lerps after collect: {Control.TotalLerpCount}
			  Custom lerps after collect: {Control.CustomLerpCount}
			  Retained assemblies: {Control.RetainedAssemblyCount}/{Options.TypeCount}
			  Retained custom types: {Control.RetainedTypeCount}/{Options.TypeCount}
			  Retained payloads: {Control.RetainedPayloadCount}/{Options.TypeCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}
			  Managed heap delta: {Control.HeapDeltaBytes:N0} bytes

			Current MAUI: custom Lerp.Lerps entries left intact
			  Total lerps before collect: {Current.TotalLerpsBeforeCollect}
			  Custom lerps before collect: {Current.CustomLerpsBeforeCollect}
			  Total lerps after collect: {Current.TotalLerpCount}
			  Custom lerps after collect: {Current.CustomLerpCount}
			  Retained assemblies: {Current.RetainedAssemblyCount}/{Options.TypeCount}
			  Retained custom types: {Current.RetainedTypeCount}/{Options.TypeCount}
			  Retained payloads: {Current.RetainedPayloadCount}/{Options.TypeCount}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			  Managed heap delta: {Current.HeapDeltaBytes:N0} bytes
			""";
	}
}
