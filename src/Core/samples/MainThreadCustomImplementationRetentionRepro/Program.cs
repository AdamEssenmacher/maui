using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Hosting;

var options = ReproOptions.Parse(args);
var probe = new MainThreadCustomImplementationRetentionProbe(options);
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

sealed class MainThreadCustomImplementationRetentionProbe
{
	static readonly FieldInfo MainThreadImplementationField =
		typeof(MainThread).GetField("s_mainThreadImplementation", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(MainThread).FullName, "s_mainThreadImplementation");

	static readonly FieldInfo DispatcherProviderCurrentField =
		typeof(DispatcherProvider).GetField("s_currentProvider", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(DispatcherProvider).FullName, "s_currentProvider");

	readonly ReproOptions _options;

	public MainThreadCustomImplementationRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearMainThreadImplementationBeforeCollect: true);
		var current = RunScenario(clearMainThreadImplementationBeforeCollect: false);

		ClearMainThreadImplementation();
		ClearDispatcherProviderCurrent();

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearMainThreadImplementationBeforeCollect)
	{
		ClearMainThreadImplementation();
		ClearDispatcherProviderCurrent();
		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var request = CreateAndDisposeAppWithDynamicDispatcherProvider(_options);
		var mainThreadImplementationBeforeCollect = GetMainThreadImplementationTypeName();

		// Remove C487's static provider root in both scenarios. The remaining current root is
		// MainThread.s_mainThreadImplementation capturing the app dispatcher.
		ClearDispatcherProviderCurrent();

		if (clearMainThreadImplementationBeforeCollect)
			ClearMainThreadImplementation();

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPayloads = CountAlive(request.PayloadRefs);
		return new ScenarioResult(
			MainThreadImplementationBeforeCollect: mainThreadImplementationBeforeCollect,
			MainThreadImplementationAfterCollect: GetMainThreadImplementationTypeName(),
			DispatcherProviderCurrentAfterCollect: GetDispatcherProviderCurrentTypeName(),
			RetainedAssemblyCount: CountAlive(request.AssemblyRefs),
			RetainedProviderTypeCount: CountAlive(request.ProviderTypeRefs),
			RetainedProviderInstanceCount: CountAlive(request.ProviderInstanceRefs),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static DynamicDispatcherProviderRequest CreateAndDisposeAppWithDynamicDispatcherProvider(ReproOptions options)
	{
		var request = DynamicDispatcherProviderRequest.Create(options);
		var providerInstance = request.ProviderInstance
			?? throw new InvalidOperationException("Dynamic provider instance was already cleared.");

		var builder = MauiApp.CreateBuilder(useDefaults: true);
		builder.Services.AddSingleton(typeof(IDispatcherProvider), providerInstance);

		using var app = builder.Build();

		request.DropStrongReferences();
		return request;
	}

	static string GetMainThreadImplementationTypeName()
	{
		return MainThreadImplementationField.GetValue(null)?.GetType().FullName ?? "<null>";
	}

	static string GetDispatcherProviderCurrentTypeName()
	{
		return DispatcherProviderCurrentField.GetValue(null)?.GetType().FullName ?? "<null>";
	}

	static void ClearMainThreadImplementation()
	{
		MainThreadImplementationField.SetValue(null, null);
	}

	static void ClearDispatcherProviderCurrent()
	{
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

sealed class DynamicDispatcherProviderRequest
{
	DynamicDispatcherProviderRequest(
		object providerInstance,
		WeakReference<Assembly> assemblyRef,
		WeakReference<Type> providerTypeRef,
		WeakReference<object> providerInstanceRef,
		WeakReference<byte[]> payloadRef)
	{
		ProviderInstance = providerInstance;
		AssemblyRefs.Add(assemblyRef);
		ProviderTypeRefs.Add(providerTypeRef);
		ProviderInstanceRefs.Add(providerInstanceRef);
		PayloadRefs.Add(payloadRef);
	}

	public object? ProviderInstance { get; private set; }

	public List<WeakReference<Assembly>> AssemblyRefs { get; } = new();

	public List<WeakReference<Type>> ProviderTypeRefs { get; } = new();

	public List<WeakReference<object>> ProviderInstanceRefs { get; } = new();

	public List<WeakReference<byte[]>> PayloadRefs { get; } = new();

	[MethodImpl(MethodImplOptions.NoInlining)]
	public static DynamicDispatcherProviderRequest Create(ReproOptions options)
	{
		var providerType = DynamicDispatcherProviderTypeFactory.CreateType();
		var payload = CreatePayload(options.PayloadBytes);
		var providerInstance = Activator.CreateInstance(providerType, payload)
			?? throw new InvalidOperationException("Failed to create dynamic dispatcher provider.");

		return new DynamicDispatcherProviderRequest(
			providerInstance,
			new WeakReference<Assembly>(providerType.Assembly, trackResurrection: false),
			new WeakReference<Type>(providerType, trackResurrection: false),
			new WeakReference<object>(providerInstance, trackResurrection: false),
			new WeakReference<byte[]>(payload, trackResurrection: false));
	}

	public void DropStrongReferences()
	{
		ProviderInstance = null;
	}

	static byte[] CreatePayload(int payloadBytes)
	{
		var payload = new byte[payloadBytes];
		for (var offset = 0; offset < payload.Length; offset += 4096)
			payload[offset] = (byte)(offset % 251);

		return payload;
	}
}

static class DynamicDispatcherProviderTypeFactory
{
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static Type CreateType()
	{
		var assemblyName = new AssemblyName("MainThreadCustomImplementationRetentionReproDynamicProvider");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

		var typeBuilder = moduleBuilder.DefineType(
			"PluginMainThreadDispatcherProvider",
			TypeAttributes.Public | TypeAttributes.Class);
		typeBuilder.AddInterfaceImplementation(typeof(IDispatcherProvider));
		typeBuilder.AddInterfaceImplementation(typeof(IDispatcher));

		var payloadField = typeBuilder.DefineField(
			"_payload",
			typeof(byte[]),
			FieldAttributes.Private);

		DefineConstructor(typeBuilder, payloadField);
		DefineGetForCurrentThread(typeBuilder);
		DefineIsDispatchRequired(typeBuilder);
		DefineDispatch(typeBuilder, nameof(IDispatcher.Dispatch));
		DefineDispatch(typeBuilder, nameof(IDispatcher.DispatchDelayed));
		DefineCreateTimer(typeBuilder);

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
		var payloadMiB = 128;
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
	string MainThreadImplementationBeforeCollect,
	string MainThreadImplementationAfterCollect,
	string DispatcherProviderCurrentAfterCollect,
	int RetainedAssemblyCount,
	int RetainedProviderTypeCount,
	int RetainedProviderInstanceCount,
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
		Control.MainThreadImplementationAfterCollect == "<null>" &&
		Control.DispatcherProviderCurrentAfterCollect == "<null>" &&
		Control.RetainedAssemblyCount == 0 &&
		Control.RetainedProviderTypeCount == 0 &&
		Control.RetainedProviderInstanceCount == 0 &&
		Control.RetainedPayloadCount == 0 &&
		Current.MainThreadImplementationAfterCollect != "<null>" &&
		Current.DispatcherProviderCurrentAfterCollect == "<null>" &&
		Current.RetainedAssemblyCount == 1 &&
		Current.RetainedProviderTypeCount == 1 &&
		Current.RetainedProviderInstanceCount == 1 &&
		Current.RetainedPayloadCount == 1;

	public override string ToString()
	{
		var writer = new StringWriter();
		writer.WriteLine("MAUI MainThread custom implementation retention repro");
		writer.WriteLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
		writer.WriteLine();
		writer.WriteLine("Trigger:");
		writer.WriteLine("  On netstandard/external TFMs, UseEssentials() registers MainThreadBridgeInitializer.");
		writer.WriteLine("  During MauiAppBuilder.Build(), the initializer resolves the app dispatcher and passes lambdas capturing it to MainThread.SetCustomImplementation(...).");
		writer.WriteLine("  MainThread stores those delegates in process-static s_mainThreadImplementation and MauiApp.Dispose() does not clear them.");
		writer.WriteLine("  This repro clears DispatcherProvider.s_currentProvider in both scenarios to isolate this from C487.");
		writer.WriteLine();
		writer.WriteLine($"Payload on dynamic dispatcher provider: {Options.PayloadBytes / 1024 / 1024} MiB");
		writer.WriteLine();
		WriteScenario(writer, "Control: MainThread.s_mainThreadImplementation cleared after app disposal and before forced GC", Control);
		writer.WriteLine();
		WriteScenario(writer, "Current MAUI: MainThread.s_mainThreadImplementation left intact after app disposal", Current);
		return writer.ToString();
	}

	static void WriteScenario(StringWriter writer, string title, ScenarioResult result)
	{
		writer.WriteLine(title);
		writer.WriteLine($"  MainThread implementation before collect: {result.MainThreadImplementationBeforeCollect}");
		writer.WriteLine($"  MainThread implementation after collect: {result.MainThreadImplementationAfterCollect}");
		writer.WriteLine($"  DispatcherProvider current after collect: {result.DispatcherProviderCurrentAfterCollect}");
		writer.WriteLine($"  Retained assemblies: {result.RetainedAssemblyCount}");
		writer.WriteLine($"  Retained provider types: {result.RetainedProviderTypeCount}");
		writer.WriteLine($"  Retained provider instances: {result.RetainedProviderInstanceCount}");
		writer.WriteLine($"  Retained payloads: {result.RetainedPayloadCount}");
		writer.WriteLine($"  Retained payload bytes: {result.RetainedPayloadBytes:N0}");
		writer.WriteLine($"  Managed heap delta: {result.HeapDeltaBytes:N0} bytes");
	}
}
