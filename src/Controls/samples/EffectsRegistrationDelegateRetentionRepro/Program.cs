using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

var options = ReproOptions.Parse(args);
var probe = new EffectsRegistrationDelegateRetentionProbe(options);
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

sealed class EffectsRegistrationDelegateRetentionProbe
{
	static readonly Type EffectsRegistrationType =
		typeof(IEffectsBuilder).Assembly.GetType("Microsoft.Maui.Controls.Hosting.EffectsRegistration", throwOnError: true)
			?? throw new TypeLoadException("Microsoft.Maui.Controls.Hosting.EffectsRegistration");

	static readonly FieldInfo RegisterEffectsField =
		EffectsRegistrationType.GetField("_registerEffects", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(EffectsRegistrationType.FullName, "_registerEffects");

	static readonly Type EnumerableEffectsRegistrationType = typeof(IEnumerable<>).MakeGenericType(EffectsRegistrationType);

	readonly ReproOptions _options;

	public EffectsRegistrationDelegateRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearDynamicRegistrationDelegatesBeforeCollect: true);
		var current = RunScenario(clearDynamicRegistrationDelegatesBeforeCollect: false);

		CollectHard();
		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearDynamicRegistrationDelegatesBeforeCollect)
	{
		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var request = CreateLiveAppWithDynamicEffectDelegates(_options);
		var dynamicDelegatesBeforeCollect = CountDynamicRegistrationDelegates(request.App.Services);
		var effectFactoryEntriesBeforeCollect = CountEffectFactoryEntriesIfResolved(request.App.Services);

		if (clearDynamicRegistrationDelegatesBeforeCollect)
			ClearDynamicRegistrationDelegates(request.App.Services);

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPayloads = CountAlive(request.PayloadRefs);
		var result = new ScenarioResult(
			DynamicRegistrationDelegatesBeforeCollect: dynamicDelegatesBeforeCollect,
			DynamicRegistrationDelegatesAfterCollect: CountDynamicRegistrationDelegates(request.App.Services),
			EffectFactoryEntriesBeforeCollect: effectFactoryEntriesBeforeCollect,
			EffectFactoryEntriesAfterCollect: CountEffectFactoryEntriesIfResolved(request.App.Services),
			RetainedAssemblyCount: CountAlive(request.AssemblyRefs),
			RetainedTargetTypeCount: CountAlive(request.TargetTypeRefs),
			RetainedTargetInstanceCount: CountAlive(request.TargetInstanceRefs),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);

		request.Dispose();
		CollectHard();

		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static DynamicEffectRegistrationRequest CreateLiveAppWithDynamicEffectDelegates(ReproOptions options)
	{
		var builder = MauiApp.CreateBuilder(useDefaults: false);
		var request = new DynamicEffectRegistrationRequest();

		for (var i = 0; i < options.RegistrationCount; i++)
			DynamicEffectRegistrationFactory.AddRegistration(builder, request, options.PayloadBytes, i);

		request.App = builder.Build();
		return request;
	}

	static int CountDynamicRegistrationDelegates(IServiceProvider services)
	{
		var count = 0;
		foreach (var registration in GetEffectRegistrations(services))
		{
			if (RegisterEffectsField.GetValue(registration) is Delegate del)
				count += CountDynamicDelegates(del);
		}

		return count;
	}

	static int CountDynamicDelegates(Delegate del)
	{
		var count = 0;
		foreach (var item in del.GetInvocationList())
		{
			if (IsDynamicRegistrationDelegate(item))
				count++;
		}

		return count;
	}

	static void ClearDynamicRegistrationDelegates(IServiceProvider services)
	{
		foreach (var registration in GetEffectRegistrations(services))
		{
			if (RegisterEffectsField.GetValue(registration) is not Delegate current)
				continue;

			var kept = current.GetInvocationList()
				.Where(item => !IsDynamicRegistrationDelegate(item))
				.ToArray();

			var replacement = kept.Length == 0
				? (Action<IEffectsBuilder>)Noop
				: Delegate.Combine(kept);

			RegisterEffectsField.SetValue(registration, replacement);
		}
	}

	static IEnumerable<object> GetEffectRegistrations(IServiceProvider services)
	{
		if (services.GetService(EnumerableEffectsRegistrationType) is not IEnumerable registrations)
			yield break;

		foreach (var registration in registrations)
		{
			if (registration is not null)
				yield return registration;
		}
	}

	static int CountEffectFactoryEntriesIfResolved(IServiceProvider services)
	{
		// Resolving EffectsFactory here verifies that the already-tracked C480
		// _registeredEffects dictionary path stays empty for this delegate-only proof.
		var factoryType = typeof(IEffectsBuilder).Assembly.GetType("Microsoft.Maui.Controls.Hosting.EffectsFactory", throwOnError: true)
			?? throw new TypeLoadException("Microsoft.Maui.Controls.Hosting.EffectsFactory");
		if (services.GetService(factoryType) is not object factory)
			return 0;

		var registeredEffectsField = factoryType.GetField("_registeredEffects", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(factoryType.FullName, "_registeredEffects");
		return registeredEffectsField.GetValue(factory) is IDictionary effects ? effects.Count : 0;
	}

	static void Noop(IEffectsBuilder _) { }

	static bool IsDynamicRegistrationDelegate(Delegate del) =>
		del.Method.DeclaringType?.Assembly.GetName().Name?.StartsWith(DynamicEffectRegistrationFactory.DynamicAssemblyPrefix, StringComparison.Ordinal) == true;

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

sealed class DynamicEffectRegistrationRequest : IDisposable
{
	public MauiApp App { get; set; } = null!;

	public List<WeakReference<Assembly>> AssemblyRefs { get; } = new();

	public List<WeakReference<Type>> TargetTypeRefs { get; } = new();

	public List<WeakReference<object>> TargetInstanceRefs { get; } = new();

	public List<WeakReference<byte[]>> PayloadRefs { get; } = new();

	public void Track(Type targetType, object targetInstance, byte[] payload)
	{
		AssemblyRefs.Add(new WeakReference<Assembly>(targetType.Assembly, trackResurrection: false));
		TargetTypeRefs.Add(new WeakReference<Type>(targetType, trackResurrection: false));
		TargetInstanceRefs.Add(new WeakReference<object>(targetInstance, trackResurrection: false));
		PayloadRefs.Add(new WeakReference<byte[]>(payload, trackResurrection: false));
	}

	public void Dispose()
	{
		App.Dispose();
	}
}

static class DynamicEffectRegistrationFactory
{
	public const string DynamicAssemblyPrefix = "EffectsRegistrationDelegateRetentionReproDynamic";

	[MethodImpl(MethodImplOptions.NoInlining)]
	public static void AddRegistration(MauiAppBuilder builder, DynamicEffectRegistrationRequest request, int payloadBytes, int index)
	{
		var targetType = CreateTargetType(index);
		var payload = CreatePayload(payloadBytes, index);
		var target = Activator.CreateInstance(targetType, payload)
			?? throw new InvalidOperationException("Failed to create dynamic effect registration target.");

		builder.ConfigureEffects((Action<IEffectsBuilder>)CreateDelegate(target, targetType));
		request.Track(targetType, target, payload);
	}

	static Delegate CreateDelegate(object target, Type targetType)
	{
		var method = targetType.GetMethod("ConfigureEffects")
			?? throw new MissingMethodException(targetType.FullName, "ConfigureEffects");
		return Delegate.CreateDelegate(typeof(Action<IEffectsBuilder>), target, method);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static Type CreateTargetType(int index)
	{
		var assemblyName = new AssemblyName($"{DynamicAssemblyPrefix}{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");
		var typeBuilder = moduleBuilder.DefineType(
			$"PluginEffectStartupRegistrationTarget{index}",
			TypeAttributes.Public | TypeAttributes.Class);

		var payloadField = typeBuilder.DefineField("_payload", typeof(byte[]), FieldAttributes.Private);
		DefineConstructor(typeBuilder, payloadField);
		DefineConfigureEffectsMethod(typeBuilder, payloadField);

		return typeBuilder.CreateType()!;
	}

	static void DefineConstructor(TypeBuilder typeBuilder, FieldInfo payloadField)
	{
		var ctor = typeBuilder.DefineConstructor(
			MethodAttributes.Public,
			CallingConventions.Standard,
			new[] { typeof(byte[]) });
		var il = ctor.GetILGenerator();
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Ldarg_1);
		il.Emit(OpCodes.Stfld, payloadField);
		il.Emit(OpCodes.Ret);
	}

	static void DefineConfigureEffectsMethod(TypeBuilder typeBuilder, FieldInfo payloadField)
	{
		var method = typeBuilder.DefineMethod(
			"ConfigureEffects",
			MethodAttributes.Public | MethodAttributes.HideBySig,
			typeof(void),
			new[] { typeof(IEffectsBuilder) });

		var il = method.GetILGenerator();
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Ldfld, payloadField);
		il.Emit(OpCodes.Pop);
		il.Emit(OpCodes.Ret);
	}

	static byte[] CreatePayload(int payloadBytes, int index)
	{
		var payload = new byte[payloadBytes];
		for (var offset = 0; offset < payload.Length; offset += 4096)
			payload[offset] = (byte)((offset + index) % 251);

		return payload;
	}
}

sealed record ScenarioResult(
	int DynamicRegistrationDelegatesBeforeCollect,
	int DynamicRegistrationDelegatesAfterCollect,
	int EffectFactoryEntriesBeforeCollect,
	int EffectFactoryEntriesAfterCollect,
	int RetainedAssemblyCount,
	int RetainedTargetTypeCount,
	int RetainedTargetInstanceCount,
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
		Control.DynamicRegistrationDelegatesAfterCollect == 0 &&
		Control.EffectFactoryEntriesAfterCollect == 0 &&
		Control.RetainedPayloadCount == 0 &&
		Current.DynamicRegistrationDelegatesAfterCollect == Options.RegistrationCount &&
		Current.EffectFactoryEntriesAfterCollect == 0 &&
		Current.RetainedAssemblyCount == Options.RegistrationCount &&
		Current.RetainedTargetTypeCount == Options.RegistrationCount &&
		Current.RetainedTargetInstanceCount == Options.RegistrationCount &&
		Current.RetainedPayloadCount == Options.RegistrationCount;

	public override string ToString()
	{
		return $"""
			MAUI EffectsRegistration startup delegate retention repro
			Result: {(Proven ? "PROVEN" : "NOT PROVEN")}

			Trigger:
			  ConfigureEffects stores startup configure delegates in singleton EffectsRegistration objects.
			  This repro's delegates intentionally perform no IEffectsBuilder.Add calls, so no dynamic effect or platform-effect types are registered.
			  The EffectsFactory _registeredEffects dictionary remains empty; this isolates the delegate target retention from C480.

			Dynamic effect startup registration targets: {Options.RegistrationCount}
			Delegates per target: 1
			Payload per target: {Options.PayloadMib} MiB

			Control: EffectsRegistration._registerEffects replaced with no-op delegates before forced GC while the app remains live
			  Dynamic registration delegates before collect: {Control.DynamicRegistrationDelegatesBeforeCollect}
			  Dynamic registration delegates after collect: {Control.DynamicRegistrationDelegatesAfterCollect}
			  EffectsFactory entries before collect: {Control.EffectFactoryEntriesBeforeCollect}
			  EffectsFactory entries after collect: {Control.EffectFactoryEntriesAfterCollect}
			  Retained assemblies: {Control.RetainedAssemblyCount}
			  Retained target types: {Control.RetainedTargetTypeCount}
			  Retained target instances: {Control.RetainedTargetInstanceCount}
			  Retained payloads: {Control.RetainedPayloadCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}
			  Managed heap delta: {Control.HeapDeltaBytes:N0} bytes

			Current MAUI: EffectsRegistration._registerEffects left intact while the app remains live
			  Dynamic registration delegates before collect: {Current.DynamicRegistrationDelegatesBeforeCollect}
			  Dynamic registration delegates after collect: {Current.DynamicRegistrationDelegatesAfterCollect}
			  EffectsFactory entries before collect: {Current.EffectFactoryEntriesBeforeCollect}
			  EffectsFactory entries after collect: {Current.EffectFactoryEntriesAfterCollect}
			  Retained assemblies: {Current.RetainedAssemblyCount}
			  Retained target types: {Current.RetainedTargetTypeCount}
			  Retained target instances: {Current.RetainedTargetInstanceCount}
			  Retained payloads: {Current.RetainedPayloadCount}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			  Managed heap delta: {Current.HeapDeltaBytes:N0} bytes
			""";
	}
}

sealed record ReproOptions(int RegistrationCount, int PayloadMib, string? ResultsPath)
{
	public int PayloadBytes => PayloadMib * 1024 * 1024;

	public static ReproOptions Parse(string[] args)
	{
		var registrationCount = 80;
		var payloadMib = 1;
		string? resultsPath = null;

		foreach (var arg in args)
		{
			if (arg.StartsWith("--registrations=", StringComparison.Ordinal))
			{
				registrationCount = int.Parse(arg["--registrations=".Length..]);
			}
			else if (arg.StartsWith("--payload-mib=", StringComparison.Ordinal))
			{
				payloadMib = int.Parse(arg["--payload-mib=".Length..]);
			}
			else if (arg.StartsWith("--results=", StringComparison.Ordinal))
			{
				resultsPath = arg["--results=".Length..];
			}
		}

		if (registrationCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(registrationCount), "Registration count must be positive.");
		if (payloadMib <= 0)
			throw new ArgumentOutOfRangeException(nameof(payloadMib), "Payload size must be positive.");

		return new ReproOptions(registrationCount, payloadMib, resultsPath);
	}
}
