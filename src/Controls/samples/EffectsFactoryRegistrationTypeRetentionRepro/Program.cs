using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Controls.Platform;

var options = ReproOptions.Parse(args);
var probe = new EffectsFactoryRegistrationTypeRetentionProbe(options);
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

sealed class EffectsFactoryRegistrationTypeRetentionProbe
{
	static readonly Assembly ControlsAssembly = typeof(RoutingEffect).Assembly;

	static readonly Type EffectsRegistrationType =
		ControlsAssembly.GetType("Microsoft.Maui.Controls.Hosting.EffectsRegistration", throwOnError: true)
			?? throw new MissingMemberException("Microsoft.Maui.Controls.Hosting.EffectsRegistration");

	static readonly Type EffectsFactoryType =
		ControlsAssembly.GetType("Microsoft.Maui.Controls.Hosting.EffectsFactory", throwOnError: true)
			?? throw new MissingMemberException("Microsoft.Maui.Controls.Hosting.EffectsFactory");

	static readonly ConstructorInfo EffectsRegistrationConstructor =
		EffectsRegistrationType.GetConstructor(new[] { typeof(Action<IEffectsBuilder>) })
			?? throw new MissingMethodException(EffectsRegistrationType.FullName, ".ctor(Action<IEffectsBuilder>)");

	static readonly ConstructorInfo EffectsFactoryConstructor =
		EffectsFactoryType.GetConstructors().Single();

	static readonly FieldInfo RegisteredEffectsField =
		EffectsFactoryType.GetField("_registeredEffects", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(EffectsFactoryType.FullName, "_registeredEffects");

	readonly ReproOptions _options;

	public EffectsFactoryRegistrationTypeRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearFactoryBeforeCollect: true);
		var current = RunScenario(clearFactoryBeforeCollect: false);

		ClearFactory(current.Factory);
		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearFactoryBeforeCollect)
	{
		var assemblyRefs = new List<WeakReference<Assembly>>(_options.EffectPairCount);
		var effectTypeRefs = new List<WeakReference<Type>>(_options.EffectPairCount);
		var platformEffectTypeRefs = new List<WeakReference<Type>>(_options.EffectPairCount);
		var payloadRefs = new List<WeakReference<byte[]>>(_options.EffectPairCount);

		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var factory = CreateFactoryWithDynamicEffects(
			assemblyRefs,
			effectTypeRefs,
			platformEffectTypeRefs,
			payloadRefs);

		var entriesBeforeCollect = CountFactoryEntries(factory);

		if (clearFactoryBeforeCollect)
			ClearFactory(factory);

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPayloads = CountAlive(payloadRefs);
		return new ScenarioResult(
			Factory: factory,
			EntriesBeforeCollect: entriesBeforeCollect,
			EntriesAfterCollect: CountFactoryEntries(factory),
			RetainedAssemblyCount: CountAlive(assemblyRefs),
			RetainedEffectTypeCount: CountAlive(effectTypeRefs),
			RetainedPlatformEffectTypeCount: CountAlive(platformEffectTypeRefs),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	object CreateFactoryWithDynamicEffects(
		List<WeakReference<Assembly>> assemblyRefs,
		List<WeakReference<Type>> effectTypeRefs,
		List<WeakReference<Type>> platformEffectTypeRefs,
		List<WeakReference<byte[]>> payloadRefs)
	{
		var registrations = Array.CreateInstance(EffectsRegistrationType, _options.EffectPairCount);

		for (var i = 0; i < _options.EffectPairCount; i++)
		{
			CreateDynamicEffectPair(
				i,
				out var effectType,
				out var platformEffectType,
				out var payload);

			assemblyRefs.Add(new WeakReference<Assembly>(effectType.Assembly, trackResurrection: false));
			effectTypeRefs.Add(new WeakReference<Type>(effectType, trackResurrection: false));
			platformEffectTypeRefs.Add(new WeakReference<Type>(platformEffectType, trackResurrection: false));
			payloadRefs.Add(new WeakReference<byte[]>(payload, trackResurrection: false));

			Action<IEffectsBuilder> register = builder => builder.Add(effectType, platformEffectType);
			registrations.SetValue(EffectsRegistrationConstructor.Invoke(new object[] { register }), i);
		}

		return EffectsFactoryConstructor.Invoke(new object[] { registrations });
	}

	void CreateDynamicEffectPair(
		int index,
		out Type effectType,
		out Type platformEffectType,
		out byte[] payload)
	{
		var assemblyName = new AssemblyName($"MauiEffectsFactoryRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

		var effectTypeBuilder = moduleBuilder.DefineType(
			$"TenantRoutingEffect{index}",
			TypeAttributes.Public | TypeAttributes.Class,
			typeof(RoutingEffect));
		effectTypeBuilder.DefineDefaultConstructor(MethodAttributes.Public);
		effectType = effectTypeBuilder.CreateType()!;

		var platformEffectTypeBuilder = moduleBuilder.DefineType(
			$"TenantPlatformEffect{index}",
			TypeAttributes.Public | TypeAttributes.Class,
			typeof(PlatformEffect));
		platformEffectTypeBuilder.DefineDefaultConstructor(MethodAttributes.Public);
		DefineEmptyOverride(platformEffectTypeBuilder, "OnAttached");
		DefineEmptyOverride(platformEffectTypeBuilder, "OnDetached");
		var payloadField = platformEffectTypeBuilder.DefineField(
			"Payload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

		platformEffectType = platformEffectTypeBuilder.CreateType()!;
		payload = new byte[_options.PayloadBytes];
		for (var offset = 0; offset < payload.Length; offset += 4096)
			payload[offset] = (byte)(index % 251);

		platformEffectType.GetField(payloadField.Name)!.SetValue(null, payload);
	}

	static void DefineEmptyOverride(TypeBuilder typeBuilder, string baseMethodName)
	{
		var baseMethod = typeof(Effect).GetMethod(
				baseMethodName,
				BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingMethodException(typeof(Effect).FullName, baseMethodName);
		var methodBuilder = typeBuilder.DefineMethod(
			baseMethod.Name,
			MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
			typeof(void),
			Type.EmptyTypes);
		methodBuilder.GetILGenerator().Emit(OpCodes.Ret);
		typeBuilder.DefineMethodOverride(methodBuilder, baseMethod);
	}

	static void ClearFactory(object factory)
	{
		GetRegisteredEffects(factory).Clear();
	}

	static int CountFactoryEntries(object factory)
	{
		return GetRegisteredEffects(factory).Count;
	}

	static IDictionary GetRegisteredEffects(object factory)
	{
		if (RegisteredEffectsField.GetValue(factory) is not IDictionary effects)
			throw new InvalidOperationException("_registeredEffects did not implement IDictionary.");

		return effects;
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

sealed record ReproOptions(int EffectPairCount, int PayloadBytes, string? ResultsPath)
{
	public static ReproOptions Parse(string[] args)
	{
		var effectPairCount = 80;
		var payloadMiB = 1;
		string? resultsPath = null;

		foreach (var arg in args)
		{
			if (arg.StartsWith("--count=", StringComparison.Ordinal))
			{
				effectPairCount = int.Parse(arg["--count=".Length..]);
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

		if (effectPairCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(effectPairCount));
		if (payloadMiB <= 0)
			throw new ArgumentOutOfRangeException(nameof(payloadMiB));

		return new ReproOptions(effectPairCount, payloadMiB * 1024 * 1024, resultsPath);
	}
}

sealed record ScenarioResult(
	object Factory,
	int EntriesBeforeCollect,
	int EntriesAfterCollect,
	int RetainedAssemblyCount,
	int RetainedEffectTypeCount,
	int RetainedPlatformEffectTypeCount,
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
		Control.EntriesAfterCollect == 0
		&& Control.RetainedAssemblyCount == 0
		&& Control.RetainedEffectTypeCount == 0
		&& Control.RetainedPlatformEffectTypeCount == 0
		&& Control.RetainedPayloadCount == 0
		&& Current.EntriesAfterCollect == Options.EffectPairCount
		&& Current.RetainedAssemblyCount == Options.EffectPairCount
		&& Current.RetainedEffectTypeCount == Options.EffectPairCount
		&& Current.RetainedPlatformEffectTypeCount == Options.EffectPairCount
		&& Current.RetainedPayloadCount == Options.EffectPairCount;

	public override string ToString()
	{
		return $"""
			EffectsFactory registration Type retention repro
			Result: {(Proven ? "PROVEN" : "NOT PROVEN")}

			Trigger:
			  ConfigureEffects creates app-lifetime EffectsRegistration entries and an EffectsFactory singleton.
			  EffectsFactory builds a _registeredEffects dictionary keyed by RoutingEffect Type.
			  Each dictionary value is a platform-effect factory delegate that captures the PlatformEffect Type.
			  There is no public unregister or eviction path for plugin/module unload while the factory lives.

			Dynamic effect pairs: {Options.EffectPairCount}
			Payload per platform effect type: {Options.PayloadBytes / 1024 / 1024} MiB

			Control: EffectsFactory._registeredEffects cleared before forced GC
			  Entries before collect: {Control.EntriesBeforeCollect}
			  Entries after collect: {Control.EntriesAfterCollect}
			  Retained assemblies: {Control.RetainedAssemblyCount}/{Options.EffectPairCount}
			  Retained RoutingEffect types: {Control.RetainedEffectTypeCount}/{Options.EffectPairCount}
			  Retained PlatformEffect types: {Control.RetainedPlatformEffectTypeCount}/{Options.EffectPairCount}
			  Retained payloads: {Control.RetainedPayloadCount}/{Options.EffectPairCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}
			  Managed heap delta: {Control.HeapDeltaBytes:N0} bytes

			Current MAUI: EffectsFactory._registeredEffects left intact
			  Entries before collect: {Current.EntriesBeforeCollect}
			  Entries after collect: {Current.EntriesAfterCollect}
			  Retained assemblies: {Current.RetainedAssemblyCount}/{Options.EffectPairCount}
			  Retained RoutingEffect types: {Current.RetainedEffectTypeCount}/{Options.EffectPairCount}
			  Retained PlatformEffect types: {Current.RetainedPlatformEffectTypeCount}/{Options.EffectPairCount}
			  Retained payloads: {Current.RetainedPayloadCount}/{Options.EffectPairCount}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			  Managed heap delta: {Current.HeapDeltaBytes:N0} bytes
			""";
	}
}
