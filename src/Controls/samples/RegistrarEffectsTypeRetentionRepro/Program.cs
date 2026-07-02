using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;

var options = ReproOptions.Parse(args);
var probe = new RegistrarEffectsTypeRetentionProbe(options);
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

sealed class RegistrarEffectsTypeRetentionProbe
{
	static readonly PropertyInfo EffectsProperty =
		typeof(Registrar).GetProperty("Effects", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingMemberException(typeof(Registrar).FullName, "Effects");

	readonly ReproOptions _options;

	public RegistrarEffectsTypeRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearEffectsBeforeCollect: true);
		var current = RunScenario(clearEffectsBeforeCollect: false);

		ClearEffects();
		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearEffectsBeforeCollect)
	{
		ClearEffects();

		var assemblyRefs = new List<WeakReference<Assembly>>(_options.EffectCount);
		var effectTypeRefs = new List<WeakReference<Type>>(_options.EffectCount);
		var payloadRefs = new List<WeakReference<byte[]>>(_options.EffectCount);

		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var i = 0; i < _options.EffectCount; i++)
			CreateDynamicEffectAndRegister(i, assemblyRefs, effectTypeRefs, payloadRefs);

		var effectsBeforeCollect = CountEffects();

		if (clearEffectsBeforeCollect)
			ClearEffects();

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPayloads = CountAlive(payloadRefs);
		return new ScenarioResult(
			EffectsBeforeCollect: effectsBeforeCollect,
			EffectsAfterCollect: CountEffects(),
			RetainedAssemblyCount: CountAlive(assemblyRefs),
			RetainedEffectTypeCount: CountAlive(effectTypeRefs),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	void CreateDynamicEffectAndRegister(
		int index,
		List<WeakReference<Assembly>> assemblyRefs,
		List<WeakReference<Type>> effectTypeRefs,
		List<WeakReference<byte[]>> payloadRefs)
	{
		var assemblyName = new AssemblyName($"MauiRegistrarEffectsRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

		var effectTypeBuilder = moduleBuilder.DefineType(
			$"TenantRoutingEffect{index}",
			TypeAttributes.Public | TypeAttributes.Class,
			typeof(RoutingEffect));
		effectTypeBuilder.DefineDefaultConstructor(MethodAttributes.Public);
		var payloadField = effectTypeBuilder.DefineField(
			"Payload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

		var effectType = effectTypeBuilder.CreateType()!;
		var payload = new byte[_options.PayloadBytes];
		for (var offset = 0; offset < payload.Length; offset += 4096)
			payload[offset] = (byte)(index % 251);

		effectType.GetField(payloadField.Name)!.SetValue(null, payload);

		assemblyRefs.Add(new WeakReference<Assembly>(effectType.Assembly, trackResurrection: false));
		effectTypeRefs.Add(new WeakReference<Type>(effectType, trackResurrection: false));
		payloadRefs.Add(new WeakReference<byte[]>(payload, trackResurrection: false));

		Registrar.RegisterEffect("MauiLeakRepro", $"TenantEffect{index}", effectType);
	}

	static void ClearEffects()
	{
		if (EffectsProperty.GetValue(null) is not IDictionary effects)
			throw new InvalidOperationException("Registrar.Effects did not implement IDictionary.");

		effects.Clear();
	}

	static int CountEffects()
	{
		if (EffectsProperty.GetValue(null) is not IDictionary effects)
			throw new InvalidOperationException("Registrar.Effects did not implement IDictionary.");

		return effects.Count;
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

sealed record ReproOptions(int EffectCount, int PayloadBytes, string? ResultsPath)
{
	public static ReproOptions Parse(string[] args)
	{
		var effectCount = 80;
		var payloadMiB = 1;
		string? resultsPath = null;

		foreach (var arg in args)
		{
			if (arg.StartsWith("--count=", StringComparison.Ordinal))
			{
				effectCount = int.Parse(arg["--count=".Length..]);
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

		if (effectCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(effectCount));
		if (payloadMiB <= 0)
			throw new ArgumentOutOfRangeException(nameof(payloadMiB));

		return new ReproOptions(effectCount, payloadMiB * 1024 * 1024, resultsPath);
	}
}

sealed record ScenarioResult(
	int EffectsBeforeCollect,
	int EffectsAfterCollect,
	int RetainedAssemblyCount,
	int RetainedEffectTypeCount,
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
		Control.EffectsAfterCollect == 0
		&& Control.RetainedAssemblyCount == 0
		&& Control.RetainedEffectTypeCount == 0
		&& Control.RetainedPayloadCount == 0
		&& Current.EffectsAfterCollect == Options.EffectCount
		&& Current.RetainedAssemblyCount == Options.EffectCount
		&& Current.RetainedEffectTypeCount == Options.EffectCount
		&& Current.RetainedPayloadCount == Options.EffectCount;

	public override string ToString()
	{
		return $"""
			Registrar.Effects type retention repro
			Result: {(Proven ? "PROVEN" : "NOT PROVEN")}

			Trigger:
			  Registrar.Effects is a process-static dictionary used by Effect.Resolve.
			  RegisterEffect and ExportEffect registration paths store effect Type values in that dictionary.
			  There is no public unregister or eviction path for plugin/module unload.

			Dynamic effect types: {Options.EffectCount}
			Payload per effect type: {Options.PayloadBytes / 1024 / 1024} MiB

			Control: Registrar.Effects cleared before forced GC
			  Effects before collect: {Control.EffectsBeforeCollect}
			  Effects after collect: {Control.EffectsAfterCollect}
			  Retained assemblies: {Control.RetainedAssemblyCount}/{Options.EffectCount}
			  Retained effect types: {Control.RetainedEffectTypeCount}/{Options.EffectCount}
			  Retained payloads: {Control.RetainedPayloadCount}/{Options.EffectCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}
			  Managed heap delta: {Control.HeapDeltaBytes:N0} bytes

			Current MAUI: Registrar.Effects left intact
			  Effects before collect: {Current.EffectsBeforeCollect}
			  Effects after collect: {Current.EffectsAfterCollect}
			  Retained assemblies: {Current.RetainedAssemblyCount}/{Options.EffectCount}
			  Retained effect types: {Current.RetainedEffectTypeCount}/{Options.EffectCount}
			  Retained payloads: {Current.RetainedPayloadCount}/{Options.EffectCount}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			  Managed heap delta: {Current.HeapDeltaBytes:N0} bytes
			""";
	}
}
