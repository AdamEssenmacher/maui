using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Hosting;

var options = ReproOptions.Parse(args);
var probe = new DispatcherProviderStaticCurrentRetentionProbe(options);
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

sealed class DispatcherProviderStaticCurrentRetentionProbe
{
	static readonly FieldInfo CurrentProviderField =
		typeof(DispatcherProvider).GetField("s_currentProvider", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(DispatcherProvider).FullName, "s_currentProvider");

	readonly ReproOptions _options;

	public DispatcherProviderStaticCurrentRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearCurrentProviderBeforeCollect: true);
		var current = RunScenario(clearCurrentProviderBeforeCollect: false);

		ClearCurrentDispatcherProvider();

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearCurrentProviderBeforeCollect)
	{
		ClearCurrentDispatcherProvider();
		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var request = CreateAndDisposeAppWithDynamicDispatcherProvider(_options);
		var currentProviderTypeBeforeCollect = GetCurrentDispatcherProviderTypeName();

		if (clearCurrentProviderBeforeCollect)
			ClearCurrentDispatcherProvider();

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedInstancePayloads = CountAlive(request.InstancePayloadRefs);
		var retainedStaticPayloads = CountAlive(request.StaticPayloadRefs);

		return new ScenarioResult(
			CurrentProviderTypeBeforeCollect: currentProviderTypeBeforeCollect,
			CurrentProviderTypeAfterCollect: GetCurrentDispatcherProviderTypeName(),
			RetainedAssemblyCount: CountAlive(request.AssemblyRefs),
			RetainedProviderTypeCount: CountAlive(request.ProviderTypeRefs),
			RetainedProviderInstanceCount: CountAlive(request.ProviderInstanceRefs),
			RetainedInstancePayloadCount: retainedInstancePayloads,
			RetainedStaticPayloadCount: retainedStaticPayloads,
			RetainedPayloadBytes: (long)(retainedInstancePayloads + retainedStaticPayloads) * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static DynamicDispatcherProviderRequest CreateAndDisposeAppWithDynamicDispatcherProvider(ReproOptions options)
	{
		var request = DynamicDispatcherProviderRequest.Create(options);
		var providerInstance = request.ProviderInstance
			?? throw new InvalidOperationException("Dynamic provider instance was already cleared.");

		var builder = MauiApp.CreateBuilder(useDefaults: false);
		builder.Services.AddSingleton(typeof(IDispatcherProvider), providerInstance);
		builder.ConfigureDispatching();

		using var app = builder.Build();

		request.DropStrongReferences();
		return request;
	}

	static string GetCurrentDispatcherProviderTypeName()
	{
		return CurrentProviderField.GetValue(null)?.GetType().FullName ?? "<null>";
	}

	static void ClearCurrentDispatcherProvider()
	{
		CurrentProviderField.SetValue(null, null);
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

sealed class DynamicDispatcherProviderRequest
{
	DynamicDispatcherProviderRequest(
		object providerInstance,
		WeakReference<Assembly> assemblyRef,
		WeakReference<Type> providerTypeRef,
		WeakReference<object> providerInstanceRef,
		WeakReference<byte[]> instancePayloadRef,
		WeakReference<byte[]> staticPayloadRef)
	{
		ProviderInstance = providerInstance;
		AssemblyRefs.Add(assemblyRef);
		ProviderTypeRefs.Add(providerTypeRef);
		ProviderInstanceRefs.Add(providerInstanceRef);
		InstancePayloadRefs.Add(instancePayloadRef);
		StaticPayloadRefs.Add(staticPayloadRef);
	}

	public object? ProviderInstance { get; private set; }

	public List<WeakReference<Assembly>> AssemblyRefs { get; } = new();

	public List<WeakReference<Type>> ProviderTypeRefs { get; } = new();

	public List<WeakReference<object>> ProviderInstanceRefs { get; } = new();

	public List<WeakReference<byte[]>> InstancePayloadRefs { get; } = new();

	public List<WeakReference<byte[]>> StaticPayloadRefs { get; } = new();

	[MethodImpl(MethodImplOptions.NoInlining)]
	public static DynamicDispatcherProviderRequest Create(ReproOptions options)
	{
		var providerType = DynamicDispatcherProviderTypeFactory.CreateType();
		var instancePayload = CreatePayload(seed: 17, options.PayloadBytes);
		var staticPayload = CreatePayload(seed: 113, options.PayloadBytes);

		providerType.GetField("StaticPayload")!.SetValue(null, staticPayload);
		var providerInstance = Activator.CreateInstance(providerType, instancePayload)
			?? throw new InvalidOperationException("Failed to create dynamic dispatcher provider.");

		return new DynamicDispatcherProviderRequest(
			providerInstance,
			new WeakReference<Assembly>(providerType.Assembly, trackResurrection: false),
			new WeakReference<Type>(providerType, trackResurrection: false),
			new WeakReference<object>(providerInstance, trackResurrection: false),
			new WeakReference<byte[]>(instancePayload, trackResurrection: false),
			new WeakReference<byte[]>(staticPayload, trackResurrection: false));
	}

	public void DropStrongReferences()
	{
		ProviderInstance = null;
	}

	static byte[] CreatePayload(int seed, int payloadBytes)
	{
		var payload = new byte[payloadBytes];
		for (var offset = 0; offset < payload.Length; offset += 4096)
			payload[offset] = (byte)((seed + offset) % 251);

		return payload;
	}
}

static class DynamicDispatcherProviderTypeFactory
{
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static Type CreateType()
	{
		var assemblyName = new AssemblyName("DispatcherProviderStaticCurrentRetentionReproDynamicProvider");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

		var typeBuilder = moduleBuilder.DefineType(
			"PluginDispatcherProvider",
			TypeAttributes.Public | TypeAttributes.Class);
		typeBuilder.AddInterfaceImplementation(typeof(IDispatcherProvider));
		typeBuilder.AddInterfaceImplementation(typeof(IDispatcher));

		var instancePayloadField = typeBuilder.DefineField(
			"_instancePayload",
			typeof(byte[]),
			FieldAttributes.Private);
		typeBuilder.DefineField(
			"StaticPayload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

		DefineConstructor(typeBuilder, instancePayloadField);
		DefineGetForCurrentThread(typeBuilder);
		DefineIsDispatchRequired(typeBuilder);
		DefineDispatch(typeBuilder, nameof(IDispatcher.Dispatch));
		DefineDispatch(typeBuilder, nameof(IDispatcher.DispatchDelayed));
		DefineCreateTimer(typeBuilder);

		return typeBuilder.CreateType()!;
	}

	static void DefineConstructor(TypeBuilder typeBuilder, FieldInfo instancePayloadField)
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
		il.Emit(OpCodes.Stfld, instancePayloadField);
		il.Emit(OpCodes.Ret);
	}

	static void DefineGetForCurrentThread(TypeBuilder typeBuilder)
	{
		var method = typeBuilder.DefineMethod(
			nameof(IDispatcherProvider.GetForCurrentThread),
			MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
			typeof(IDispatcher),
			Type.EmptyTypes);
		var il = method.GetILGenerator();
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Ret);

		typeBuilder.DefineMethodOverride(method, typeof(IDispatcherProvider).GetMethod(nameof(IDispatcherProvider.GetForCurrentThread))!);
	}

	static void DefineIsDispatchRequired(TypeBuilder typeBuilder)
	{
		var property = typeBuilder.DefineProperty(
			nameof(IDispatcher.IsDispatchRequired),
			PropertyAttributes.None,
			typeof(bool),
			Type.EmptyTypes);
		var getter = typeBuilder.DefineMethod(
			"get_" + nameof(IDispatcher.IsDispatchRequired),
			MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.NewSlot | MethodAttributes.Final,
			typeof(bool),
			Type.EmptyTypes);
		var il = getter.GetILGenerator();
		il.Emit(OpCodes.Ldc_I4_0);
		il.Emit(OpCodes.Ret);
		property.SetGetMethod(getter);

		typeBuilder.DefineMethodOverride(getter, typeof(IDispatcher).GetProperty(nameof(IDispatcher.IsDispatchRequired))!.GetMethod!);
	}

	static void DefineDispatch(TypeBuilder typeBuilder, string methodName)
	{
		var parameterTypes = methodName == nameof(IDispatcher.Dispatch)
			? new[] { typeof(Action) }
			: new[] { typeof(TimeSpan), typeof(Action) };
		var actionParameterIndex = methodName == nameof(IDispatcher.Dispatch) ? 1 : 2;

		var method = typeBuilder.DefineMethod(
			methodName,
			MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
			typeof(bool),
			parameterTypes);
		var il = method.GetILGenerator();
		il.Emit(OpCodes.Ldarg, actionParameterIndex);
		il.Emit(OpCodes.Callvirt, typeof(Action).GetMethod(nameof(Action.Invoke))!);
		il.Emit(OpCodes.Ldc_I4_1);
		il.Emit(OpCodes.Ret);

		typeBuilder.DefineMethodOverride(method, typeof(IDispatcher).GetMethod(methodName, parameterTypes)!);
	}

	static void DefineCreateTimer(TypeBuilder typeBuilder)
	{
		var method = typeBuilder.DefineMethod(
			nameof(IDispatcher.CreateTimer),
			MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
			typeof(IDispatcherTimer),
			Type.EmptyTypes);
		var il = method.GetILGenerator();
		il.Emit(OpCodes.Ldnull);
		il.Emit(OpCodes.Ret);

		typeBuilder.DefineMethodOverride(method, typeof(IDispatcher).GetMethod(nameof(IDispatcher.CreateTimer))!);
	}
}

sealed record ReproOptions(int PayloadBytes, string? ResultsPath)
{
	public static ReproOptions Parse(string[] args)
	{
		var payloadMiB = 64;
		string? resultsPath = null;

		foreach (var arg in args)
		{
			if (arg.StartsWith("--payload-mib=", StringComparison.Ordinal))
			{
				payloadMiB = int.Parse(arg["--payload-mib=".Length..]);
			}
			else if (arg.StartsWith("--results=", StringComparison.Ordinal))
			{
				resultsPath = arg["--results=".Length..];
			}
		}

		if (payloadMiB <= 0)
			throw new ArgumentOutOfRangeException(nameof(payloadMiB));

		return new ReproOptions(payloadMiB * 1024 * 1024, resultsPath);
	}
}

sealed record ScenarioResult(
	string CurrentProviderTypeBeforeCollect,
	string CurrentProviderTypeAfterCollect,
	int RetainedAssemblyCount,
	int RetainedProviderTypeCount,
	int RetainedProviderInstanceCount,
	int RetainedInstancePayloadCount,
	int RetainedStaticPayloadCount,
	long RetainedPayloadBytes,
	long HeapBeforeBytes,
	long HeapAfterBytes)
{
	public long HeapDeltaBytes => HeapAfterBytes - HeapBeforeBytes;
}

sealed record ReproReport(ReproOptions Options, ScenarioResult Control, ScenarioResult Current)
{
	public bool Proven =>
		Control.CurrentProviderTypeAfterCollect == "<null>" &&
		Control.RetainedAssemblyCount == 0 &&
		Control.RetainedProviderTypeCount == 0 &&
		Control.RetainedProviderInstanceCount == 0 &&
		Control.RetainedInstancePayloadCount == 0 &&
		Control.RetainedStaticPayloadCount == 0 &&
		Current.CurrentProviderTypeAfterCollect == "PluginDispatcherProvider" &&
		Current.RetainedAssemblyCount == 1 &&
		Current.RetainedProviderTypeCount == 1 &&
		Current.RetainedProviderInstanceCount == 1 &&
		Current.RetainedInstancePayloadCount == 1 &&
		Current.RetainedStaticPayloadCount == 1;

	public override string ToString()
	{
		var writer = new StringWriter();
		writer.WriteLine("MAUI DispatcherProvider static-current retention repro");
		writer.WriteLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
		writer.WriteLine();
		writer.WriteLine("Trigger:");
		writer.WriteLine("  ConfigureDispatching() resolves the app's registered IDispatcherProvider during MauiAppBuilder.Build().");
		writer.WriteLine("  GetDispatcher(...) copies that provider into DispatcherProvider.s_currentProvider via DispatcherProvider.SetCurrent(provider).");
		writer.WriteLine("  Disposing the MauiApp does not clear the process-static current provider.");
		writer.WriteLine("  A custom provider registered by an embedding, test, plugin, or nonstandard backend can therefore remain rooted after the app is disposed.");
		writer.WriteLine();
		writer.WriteLine($"Instance payload on dynamic provider: {Options.PayloadBytes / 1024 / 1024} MiB");
		writer.WriteLine($"Static payload on dynamic provider type: {Options.PayloadBytes / 1024 / 1024} MiB");
		writer.WriteLine();
		WriteScenario(writer, "Control: DispatcherProvider.s_currentProvider cleared after app disposal and before forced GC", Control);
		writer.WriteLine();
		WriteScenario(writer, "Current MAUI: DispatcherProvider.s_currentProvider left intact after app disposal", Current);
		return writer.ToString();
	}

	static void WriteScenario(StringWriter writer, string title, ScenarioResult result)
	{
		writer.WriteLine(title);
		writer.WriteLine($"  Current provider before collect: {result.CurrentProviderTypeBeforeCollect}");
		writer.WriteLine($"  Current provider after collect: {result.CurrentProviderTypeAfterCollect}");
		writer.WriteLine($"  Retained assemblies: {result.RetainedAssemblyCount}");
		writer.WriteLine($"  Retained provider types: {result.RetainedProviderTypeCount}");
		writer.WriteLine($"  Retained provider instances: {result.RetainedProviderInstanceCount}");
		writer.WriteLine($"  Retained instance payloads: {result.RetainedInstancePayloadCount}");
		writer.WriteLine($"  Retained static payloads: {result.RetainedStaticPayloadCount}");
		writer.WriteLine($"  Retained payload bytes: {result.RetainedPayloadBytes:N0}");
		writer.WriteLine($"  Managed heap delta: {result.HeapDeltaBytes:N0} bytes");
	}
}
