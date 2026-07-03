using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;

var options = ReproOptions.Parse(args);
var probe = new StartupRegistrationDelegateRetentionProbe(options);
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

sealed class StartupRegistrationDelegateRetentionProbe
{
	static readonly FieldInfo MainThreadImplementationField =
		typeof(MainThread).GetField("s_mainThreadImplementation", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(MainThread).FullName, "s_mainThreadImplementation");

	static readonly FieldInfo DispatcherProviderCurrentField =
		typeof(DispatcherProvider).GetField("s_currentProvider", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(DispatcherProvider).FullName, "s_currentProvider");

	static readonly RegistrationSlot[] RegistrationSlots =
	[
		RegistrationSlot.Create("Microsoft.Maui.Hosting.FontsMauiAppBuilderExtensions+FontsRegistration", "_registerFonts"),
		RegistrationSlot.Create("Microsoft.Maui.Hosting.HandlerMauiAppBuilderExtensions+HandlerRegistration", "_registerAction"),
		RegistrationSlot.Create("Microsoft.Maui.Hosting.ImageSourcesMauiAppBuilderExtensions+ImageSourceRegistration", "_registerAction"),
		RegistrationSlot.Create("Microsoft.Maui.LifecycleEvents.LifecycleEventRegistration", "_registerAction"),
		RegistrationSlot.Create("Microsoft.Maui.Hosting.EssentialsExtensions+EssentialsRegistration", "_registerEssentials"),
	];

	readonly ReproOptions _options;

	public StartupRegistrationDelegateRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearDynamicRegistrationDelegatesBeforeCollect: true);
		var current = RunScenario(clearDynamicRegistrationDelegatesBeforeCollect: false);

		ClearProcessStatics();
		CollectHard();

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearDynamicRegistrationDelegatesBeforeCollect)
	{
		ClearProcessStatics();
		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var request = CreateLiveAppWithDynamicStartupDelegates(_options);
		var dynamicDelegateFieldsBeforeClear = CountDynamicRegistrationDelegates(request.App.Services);

		if (clearDynamicRegistrationDelegatesBeforeCollect)
			ClearDynamicRegistrationDelegates(request.App.Services);

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPayloads = CountAlive(request.PayloadRefs);
		var result = new ScenarioResult(
			DynamicRegistrationDelegatesBeforeCollect: dynamicDelegateFieldsBeforeClear,
			DynamicRegistrationDelegatesAfterCollect: CountDynamicRegistrationDelegates(request.App.Services),
			RetainedAssemblyCount: CountAlive(request.AssemblyRefs),
			RetainedTargetTypeCount: CountAlive(request.TargetTypeRefs),
			RetainedTargetInstanceCount: CountAlive(request.TargetInstanceRefs),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);

		request.Dispose();
		ClearProcessStatics();
		CollectHard();

		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static DynamicStartupRegistrationRequest CreateLiveAppWithDynamicStartupDelegates(ReproOptions options)
	{
		var builder = MauiApp.CreateBuilder(useDefaults: true);
		var request = new DynamicStartupRegistrationRequest();

		for (var i = 0; i < options.RegistrationCount; i++)
			DynamicStartupRegistrationFactory.AddRegistration(builder, request, options.PayloadBytes, i);

		request.App = builder.Build();
		return request;
	}

	static int CountDynamicRegistrationDelegates(IServiceProvider services)
	{
		var count = 0;
		foreach (var slot in RegistrationSlots)
		{
			foreach (var registration in slot.GetRegistrations(services))
			{
				if (slot.Field.GetValue(registration) is Delegate del)
					count += CountDynamicDelegates(del);
			}
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
		foreach (var slot in RegistrationSlots)
		{
			foreach (var registration in slot.GetRegistrations(services))
			{
				if (slot.Field.GetValue(registration) is not Delegate current)
					continue;

				var kept = current.GetInvocationList()
					.Where(item => !IsDynamicRegistrationDelegate(item))
					.ToArray();

				var replacement = kept.Length == 0
					? CreateNoopDelegate(slot.Field.FieldType)
					: Delegate.Combine(kept);

				slot.Field.SetValue(registration, replacement);
			}
		}
	}

	static Delegate CreateNoopDelegate(Type delegateType)
	{
		var parameterType = delegateType.GetMethod(nameof(Action.Invoke))!.GetParameters()[0].ParameterType;
		var method = typeof(StartupRegistrationDelegateRetentionProbe)
			.GetMethod(nameof(Noop), BindingFlags.Static | BindingFlags.NonPublic)!
			.MakeGenericMethod(parameterType);

		return Delegate.CreateDelegate(delegateType, method);
	}

	static void Noop<T>(T _) { }

	static bool IsDynamicRegistrationDelegate(Delegate del) =>
		del.Method.DeclaringType?.Assembly.GetName().Name?.StartsWith(DynamicStartupRegistrationFactory.DynamicAssemblyPrefix, StringComparison.Ordinal) == true;

	static void ClearProcessStatics()
	{
		MainThreadImplementationField.SetValue(null, null);
		DispatcherProviderCurrentField.SetValue(null, null);
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

sealed class RegistrationSlot
{
	RegistrationSlot(Type type, FieldInfo field)
	{
		Type = type;
		Field = field;
	}

	public Type Type { get; }

	public FieldInfo Field { get; }

	public static RegistrationSlot Create(string typeName, string fieldName)
	{
		var type = typeof(MauiApp).Assembly.GetType(typeName)
			?? throw new TypeLoadException(typeName);
		var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(type.FullName, fieldName);
		return new RegistrationSlot(type, field);
	}

	public IEnumerable<object> GetRegistrations(IServiceProvider services)
	{
		var enumerableType = typeof(IEnumerable<>).MakeGenericType(Type);
		if (services.GetService(enumerableType) is not IEnumerable registrations)
			yield break;

		foreach (var registration in registrations)
		{
			if (registration is not null)
				yield return registration;
		}
	}
}

sealed class DynamicStartupRegistrationRequest : IDisposable
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

static class DynamicStartupRegistrationFactory
{
	public const string DynamicAssemblyPrefix = "StartupRegistrationDelegateRetentionReproDynamic";

	[MethodImpl(MethodImplOptions.NoInlining)]
	public static void AddRegistration(MauiAppBuilder builder, DynamicStartupRegistrationRequest request, int payloadBytes, int index)
	{
		var targetType = CreateTargetType(index);
		var payload = CreatePayload(payloadBytes, index);
		var target = Activator.CreateInstance(targetType, payload)
			?? throw new InvalidOperationException("Failed to create dynamic startup registration target.");

		builder.ConfigureFonts((Action<IFontCollection>)CreateDelegate(target, targetType, "ConfigureFonts", typeof(Action<IFontCollection>)));
		builder.ConfigureMauiHandlers((Action<IMauiHandlersCollection>)CreateDelegate(target, targetType, "ConfigureHandlers", typeof(Action<IMauiHandlersCollection>)));
		builder.ConfigureImageSources((Action<IImageSourceServiceCollection>)CreateDelegate(target, targetType, "ConfigureImageSources", typeof(Action<IImageSourceServiceCollection>)));
		builder.ConfigureLifecycleEvents((Action<ILifecycleBuilder>)CreateDelegate(target, targetType, "ConfigureLifecycle", typeof(Action<ILifecycleBuilder>)));
		builder.ConfigureEssentials((Action<IEssentialsBuilder>)CreateDelegate(target, targetType, "ConfigureEssentials", typeof(Action<IEssentialsBuilder>)));

		request.Track(targetType, target, payload);
	}

	static Delegate CreateDelegate(object target, Type targetType, string methodName, Type delegateType)
	{
		var method = targetType.GetMethod(methodName)
			?? throw new MissingMethodException(targetType.FullName, methodName);
		return Delegate.CreateDelegate(delegateType, target, method);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static Type CreateTargetType(int index)
	{
		var assemblyName = new AssemblyName($"{DynamicAssemblyPrefix}{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");
		var typeBuilder = moduleBuilder.DefineType(
			$"PluginStartupRegistrationTarget{index}",
			TypeAttributes.Public | TypeAttributes.Class);

		var payloadField = typeBuilder.DefineField("_payload", typeof(byte[]), FieldAttributes.Private);
		DefineConstructor(typeBuilder, payloadField);
		DefineConfigureMethod(typeBuilder, payloadField, "ConfigureFonts", typeof(IFontCollection));
		DefineConfigureMethod(typeBuilder, payloadField, "ConfigureHandlers", typeof(IMauiHandlersCollection));
		DefineConfigureMethod(typeBuilder, payloadField, "ConfigureImageSources", typeof(IImageSourceServiceCollection));
		DefineConfigureMethod(typeBuilder, payloadField, "ConfigureLifecycle", typeof(ILifecycleBuilder));
		DefineConfigureMethod(typeBuilder, payloadField, "ConfigureEssentials", typeof(IEssentialsBuilder));

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

	static void DefineConfigureMethod(TypeBuilder typeBuilder, FieldInfo payloadField, string name, Type parameterType)
	{
		var method = typeBuilder.DefineMethod(
			name,
			MethodAttributes.Public | MethodAttributes.HideBySig,
			typeof(void),
			new[] { parameterType });

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
		Control.RetainedPayloadCount == 0 &&
		Current.DynamicRegistrationDelegatesAfterCollect == Options.RegistrationCount * 5 &&
		Current.RetainedAssemblyCount == Options.RegistrationCount &&
		Current.RetainedTargetTypeCount == Options.RegistrationCount &&
		Current.RetainedTargetInstanceCount == Options.RegistrationCount &&
		Current.RetainedPayloadCount == Options.RegistrationCount;

	public override string ToString()
	{
		return $"""
			MAUI startup registration delegate retention repro
			Result: {(Proven ? "PROVEN" : "NOT PROVEN")}

			Trigger:
			  ConfigureFonts, ConfigureMauiHandlers, ConfigureImageSources, ConfigureLifecycleEvents, and ConfigureEssentials store startup configure delegates in singleton registration objects.
			  MauiAppBuilder.Build() runs those delegates to copy useful registration state into runtime services, but the original registration objects and delegate targets remain in the live app service provider.
			  This repro uses no dynamic handler/image-source/effect/font registrations, so C473/C480/C482/C484/C485 runtime metadata roots are not required for the retained graph.

			Dynamic startup registration targets: {Options.RegistrationCount}
			Delegates per target: 5
			Payload per target: {Options.PayloadMib} MiB

			Control: dynamic startup registration delegate fields replaced with no-op delegates before forced GC while the app remains live
			  Dynamic registration delegates before collect: {Control.DynamicRegistrationDelegatesBeforeCollect}
			  Dynamic registration delegates after collect: {Control.DynamicRegistrationDelegatesAfterCollect}
			  Retained assemblies: {Control.RetainedAssemblyCount}
			  Retained target types: {Control.RetainedTargetTypeCount}
			  Retained target instances: {Control.RetainedTargetInstanceCount}
			  Retained payloads: {Control.RetainedPayloadCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}
			  Managed heap delta: {Control.HeapDeltaBytes:N0} bytes

			Current MAUI: startup registration delegate fields left intact while the app remains live
			  Dynamic registration delegates before collect: {Current.DynamicRegistrationDelegatesBeforeCollect}
			  Dynamic registration delegates after collect: {Current.DynamicRegistrationDelegatesAfterCollect}
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
		var registrationCount = 40;
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
