using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.HotReload;

var options = ReproOptions.Parse(args);
var probe = new HotReloadHandlerServiceRetentionProbe(options);
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

sealed class HotReloadHandlerServiceRetentionProbe
{
	readonly ReproOptions _options;

	public HotReloadHandlerServiceRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearHotReloadHandlerServiceBeforeCollect: true);
		var current = RunScenario(clearHotReloadHandlerServiceBeforeCollect: false);

		ClearHotReloadHandlerService();
		ClearRegisteredHandlerServiceTypeSetInstances();

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearHotReloadHandlerServiceBeforeCollect)
	{
		ClearHotReloadHandlerService();
		ClearRegisteredHandlerServiceTypeSetInstances();
		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var request = CreateAndDisposeApp(_options);
		var hotReloadDescriptorCountBeforeCollect = GetHotReloadHandlerServiceDescriptorCount();
		var registeredTypeSetInstancesBeforeCollect = GetRegisteredHandlerServiceTypeSetInstanceCount();

		// Remove C024's static dictionary root in both scenarios. The remaining current root is
		// MauiHotReloadHelper.HandlerService.
		ClearRegisteredHandlerServiceTypeSetInstances();

		if (clearHotReloadHandlerServiceBeforeCollect)
			ClearHotReloadHandlerService();

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedViewPayloads = CountAlive(request.ViewPayloadRefs);
		var retainedHandlerPayloads = CountAlive(request.HandlerPayloadRefs);

		return new ScenarioResult(
			HotReloadDescriptorCountBeforeCollect: hotReloadDescriptorCountBeforeCollect,
			HotReloadDescriptorCountAfterCollect: GetHotReloadHandlerServiceDescriptorCount(),
			RegisteredTypeSetInstancesBeforeCollect: registeredTypeSetInstancesBeforeCollect,
			RegisteredTypeSetInstancesAfterCollect: GetRegisteredHandlerServiceTypeSetInstanceCount(),
			RetainedAssemblyCount: CountAlive(request.AssemblyRefs),
			RetainedViewTypeCount: CountAlive(request.ViewTypeRefs),
			RetainedHandlerTypeCount: CountAlive(request.HandlerTypeRefs),
			RetainedViewPayloadCount: retainedViewPayloads,
			RetainedHandlerPayloadCount: retainedHandlerPayloads,
			RetainedPayloadBytes: (long)(retainedViewPayloads + retainedHandlerPayloads) * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static DynamicHandlerRegistrationRequest CreateAndDisposeApp(ReproOptions options)
	{
		var request = new DynamicHandlerRegistrationRequest(options);
		RegistrationState.Current = request;
		try
		{
			var builder = MauiApp.CreateBuilder(useDefaults: false);
			builder.ConfigureMauiHandlers(static handlers =>
			{
				var current = RegistrationState.Current
					?? throw new InvalidOperationException("No active handler registration request.");
				current.Register(handlers);
			});

			using var app = builder.Build();
			_ = app.Services.GetRequiredService<IMauiHandlersCollection>();
			return request;
		}
		finally
		{
			RegistrationState.Current = null;
		}
	}

	static int GetHotReloadHandlerServiceDescriptorCount()
	{
		return GetHotReloadHandlerService() is IMauiHandlersCollection handlers
			? handlers.Count
			: 0;
	}

	static object? GetHotReloadHandlerService()
	{
		var field = typeof(MauiHotReloadHelper).GetField("HandlerService", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(MauiHotReloadHelper).FullName, "HandlerService");

		return field.GetValue(null);
	}

	static void ClearHotReloadHandlerService()
	{
		var field = typeof(MauiHotReloadHelper).GetField("HandlerService", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(MauiHotReloadHelper).FullName, "HandlerService");

		field.SetValue(null, null);
	}

	static int GetRegisteredHandlerServiceTypeSetInstanceCount()
	{
		var dictionary = GetRegisteredHandlerServiceTypeSetInstances();
		return dictionary.GetType().GetProperty("Count")?.GetValue(dictionary) is int count
			? count
			: -1;
	}

	static void ClearRegisteredHandlerServiceTypeSetInstances()
	{
		var dictionary = GetRegisteredHandlerServiceTypeSetInstances();
		dictionary.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(dictionary, null);
	}

	static object GetRegisteredHandlerServiceTypeSetInstances()
	{
		var type = typeof(IMauiHandlersCollection).Assembly.GetType("Microsoft.Maui.Hosting.Internal.RegisteredHandlerServiceTypeSet")
			?? throw new InvalidOperationException("RegisteredHandlerServiceTypeSet was not found.");
		var field = type.GetField("s_instances", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingFieldException(type.FullName, "s_instances");

		return field.GetValue(null)
			?? throw new InvalidOperationException("RegisteredHandlerServiceTypeSet.s_instances was null.");
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

sealed class DynamicHandlerRegistrationRequest
{
	readonly ReproOptions _options;

	public DynamicHandlerRegistrationRequest(ReproOptions options)
	{
		_options = options;
	}

	public List<WeakReference<Assembly>> AssemblyRefs { get; } = new();

	public List<WeakReference<Type>> ViewTypeRefs { get; } = new();

	public List<WeakReference<Type>> HandlerTypeRefs { get; } = new();

	public List<WeakReference<byte[]>> ViewPayloadRefs { get; } = new();

	public List<WeakReference<byte[]>> HandlerPayloadRefs { get; } = new();

	[MethodImpl(MethodImplOptions.NoInlining)]
	public void Register(IMauiHandlersCollection handlers)
	{
		for (var i = 0; i < _options.RegistrationCount; i++)
		{
			DynamicHandlerTypeFactory.Create(
				i,
				_options.PayloadBytes,
				out var viewType,
				out var handlerType,
				out var viewPayload,
				out var handlerPayload);

			AssemblyRefs.Add(new WeakReference<Assembly>(viewType.Assembly, trackResurrection: false));
			ViewTypeRefs.Add(new WeakReference<Type>(viewType, trackResurrection: false));
			HandlerTypeRefs.Add(new WeakReference<Type>(handlerType, trackResurrection: false));
			ViewPayloadRefs.Add(new WeakReference<byte[]>(viewPayload, trackResurrection: false));
			HandlerPayloadRefs.Add(new WeakReference<byte[]>(handlerPayload, trackResurrection: false));

			handlers.AddHandler(viewType, handlerType);
		}
	}
}

static class RegistrationState
{
	public static DynamicHandlerRegistrationRequest? Current;
}

static class DynamicHandlerTypeFactory
{
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static void Create(
		int index,
		int payloadBytes,
		out Type viewType,
		out Type handlerType,
		out byte[] viewPayload,
		out byte[] handlerPayload)
	{
		var assemblyName = new AssemblyName($"HotReloadHandlerServiceRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

		viewType = CreateViewType(moduleBuilder, index);
		handlerType = CreateHandlerType(moduleBuilder, index);

		viewPayload = CreatePayload(index, payloadBytes);
		handlerPayload = CreatePayload(index + 131, payloadBytes);

		viewType.GetField("ViewPayload")!.SetValue(null, viewPayload);
		handlerType.GetField("HandlerPayload")!.SetValue(null, handlerPayload);
	}

	static Type CreateViewType(ModuleBuilder moduleBuilder, int index)
	{
		var typeBuilder = moduleBuilder.DefineType(
			$"PluginView{index}",
			TypeAttributes.Public | TypeAttributes.Class);
		typeBuilder.AddInterfaceImplementation(typeof(IElement));
		DefineDefaultConstructor(typeBuilder);

		typeBuilder.DefineField(
			"ViewPayload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

		var handlerField = typeBuilder.DefineField(
			"_handler",
			typeof(IElementHandler),
			FieldAttributes.Private);

		var handlerProperty = typeBuilder.DefineProperty(
			nameof(IElement.Handler),
			PropertyAttributes.None,
			typeof(IElementHandler),
			Type.EmptyTypes);
		var getHandler = DefineGetter(typeBuilder, "get_" + nameof(IElement.Handler), typeof(IElementHandler), il =>
		{
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldfld, handlerField);
			il.Emit(OpCodes.Ret);
		});
		var setHandler = typeBuilder.DefineMethod(
			"set_" + nameof(IElement.Handler),
			MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
			typeof(void),
			new[] { typeof(IElementHandler) });
		var setHandlerIl = setHandler.GetILGenerator();
		setHandlerIl.Emit(OpCodes.Ldarg_0);
		setHandlerIl.Emit(OpCodes.Ldarg_1);
		setHandlerIl.Emit(OpCodes.Stfld, handlerField);
		setHandlerIl.Emit(OpCodes.Ret);
		handlerProperty.SetGetMethod(getHandler);
		handlerProperty.SetSetMethod(setHandler);

		var parentProperty = typeBuilder.DefineProperty(
			nameof(IElement.Parent),
			PropertyAttributes.None,
			typeof(IElement),
			Type.EmptyTypes);
		var getParent = DefineGetter(typeBuilder, "get_" + nameof(IElement.Parent), typeof(IElement), il =>
		{
			il.Emit(OpCodes.Ldnull);
			il.Emit(OpCodes.Ret);
		});
		parentProperty.SetGetMethod(getParent);

		var elementType = typeof(IElement);
		typeBuilder.DefineMethodOverride(getHandler, elementType.GetProperty(nameof(IElement.Handler))!.GetMethod!);
		typeBuilder.DefineMethodOverride(setHandler, elementType.GetProperty(nameof(IElement.Handler))!.SetMethod!);
		typeBuilder.DefineMethodOverride(getParent, elementType.GetProperty(nameof(IElement.Parent))!.GetMethod!);

		return typeBuilder.CreateType()!;
	}

	static Type CreateHandlerType(ModuleBuilder moduleBuilder, int index)
	{
		var typeBuilder = moduleBuilder.DefineType(
			$"PluginHandler{index}",
			TypeAttributes.Public | TypeAttributes.Class);
		typeBuilder.AddInterfaceImplementation(typeof(IElementHandler));
		DefineDefaultConstructor(typeBuilder);

		typeBuilder.DefineField(
			"HandlerPayload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

		DefineNoOpMethod(typeBuilder, nameof(IElementHandler.SetMauiContext), new[] { typeof(IMauiContext) });
		DefineNoOpMethod(typeBuilder, nameof(IElementHandler.SetVirtualView), new[] { typeof(IElement) });
		DefineNoOpMethod(typeBuilder, nameof(IElementHandler.UpdateValue), new[] { typeof(string) });
		DefineNoOpMethod(typeBuilder, nameof(IElementHandler.Invoke), new[] { typeof(string), typeof(object) });
		DefineNoOpMethod(typeBuilder, nameof(IElementHandler.DisconnectHandler), Type.EmptyTypes);

		DefineNullGetterProperty(typeBuilder, nameof(IElementHandler.PlatformView), typeof(object));
		DefineNullGetterProperty(typeBuilder, nameof(IElementHandler.VirtualView), typeof(IElement));
		DefineNullGetterProperty(typeBuilder, nameof(IElementHandler.MauiContext), typeof(IMauiContext));

		return typeBuilder.CreateType()!;
	}

	static void DefineDefaultConstructor(TypeBuilder typeBuilder)
	{
		var ctor = typeBuilder.DefineConstructor(
			MethodAttributes.Public,
			CallingConventions.Standard,
			Type.EmptyTypes);
		var il = ctor.GetILGenerator();
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
		il.Emit(OpCodes.Ret);
	}

	static MethodBuilder DefineGetter(
		TypeBuilder typeBuilder,
		string methodName,
		Type returnType,
		Action<ILGenerator> emitBody)
	{
		var method = typeBuilder.DefineMethod(
			methodName,
			MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
			returnType,
			Type.EmptyTypes);
		emitBody(method.GetILGenerator());
		return method;
	}

	static void DefineNoOpMethod(TypeBuilder typeBuilder, string methodName, Type[] parameterTypes)
	{
		var method = typeBuilder.DefineMethod(
			methodName,
			MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
			typeof(void),
			parameterTypes);
		method.GetILGenerator().Emit(OpCodes.Ret);
		var interfaceMethod = typeof(IElementHandler).GetMethod(methodName, parameterTypes)
			?? throw new MissingMethodException(typeof(IElementHandler).FullName, methodName);
		typeBuilder.DefineMethodOverride(method, interfaceMethod);
	}

	static void DefineNullGetterProperty(TypeBuilder typeBuilder, string propertyName, Type propertyType)
	{
		var property = typeBuilder.DefineProperty(
			propertyName,
			PropertyAttributes.None,
			propertyType,
			Type.EmptyTypes);
		var getter = DefineGetter(typeBuilder, "get_" + propertyName, propertyType, il =>
		{
			il.Emit(OpCodes.Ldnull);
			il.Emit(OpCodes.Ret);
		});
		property.SetGetMethod(getter);
		typeBuilder.DefineMethodOverride(getter, typeof(IElementHandler).GetProperty(propertyName)!.GetMethod!);
	}

	static byte[] CreatePayload(int index, int payloadBytes)
	{
		var payload = new byte[payloadBytes];
		for (var offset = 0; offset < payload.Length; offset += 4096)
			payload[offset] = (byte)(index % 251);

		return payload;
	}
}

sealed record ReproOptions(int RegistrationCount, int PayloadBytes, string? ResultsPath)
{
	public static ReproOptions Parse(string[] args)
	{
		var registrationCount = 80;
		var payloadMiB = 1;
		string? resultsPath = null;

		foreach (var arg in args)
		{
			if (arg.StartsWith("--count=", StringComparison.Ordinal))
			{
				registrationCount = int.Parse(arg["--count=".Length..]);
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

		if (registrationCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(registrationCount));
		if (payloadMiB <= 0)
			throw new ArgumentOutOfRangeException(nameof(payloadMiB));

		return new ReproOptions(registrationCount, payloadMiB * 1024 * 1024, resultsPath);
	}
}

sealed record ScenarioResult(
	int HotReloadDescriptorCountBeforeCollect,
	int HotReloadDescriptorCountAfterCollect,
	int RegisteredTypeSetInstancesBeforeCollect,
	int RegisteredTypeSetInstancesAfterCollect,
	int RetainedAssemblyCount,
	int RetainedViewTypeCount,
	int RetainedHandlerTypeCount,
	int RetainedViewPayloadCount,
	int RetainedHandlerPayloadCount,
	long RetainedPayloadBytes,
	long HeapBeforeBytes,
	long HeapAfterBytes)
{
	public long HeapDeltaBytes => HeapAfterBytes - HeapBeforeBytes;
}

sealed record ReproReport(ReproOptions Options, ScenarioResult Control, ScenarioResult Current)
{
	public bool Proven =>
		Control.RetainedAssemblyCount == 0 &&
		Control.RetainedViewTypeCount == 0 &&
		Control.RetainedHandlerTypeCount == 0 &&
		Control.RetainedViewPayloadCount == 0 &&
		Control.RetainedHandlerPayloadCount == 0 &&
		Current.RetainedAssemblyCount == Options.RegistrationCount &&
		Current.RetainedViewTypeCount == Options.RegistrationCount &&
		Current.RetainedHandlerTypeCount == Options.RegistrationCount &&
		Current.RetainedViewPayloadCount == Options.RegistrationCount &&
		Current.RetainedHandlerPayloadCount == Options.RegistrationCount;

	public override string ToString() =>
		$"""
		MAUI Hot Reload handler-service retention repro
		Result: {(Proven ? "PROVEN" : "NOT PROVEN")}

		Trigger:
		  HandlerServiceBuilder always calls MauiHotReloadHelper.RegisterHandlers(this), even when Hot Reload is not enabled.
		  RegisterHandlers stores the app's IMauiHandlersCollection in the process-static HandlerService field.
		  After a throwaway MauiApp is disposed, that static last-value field can keep the disposed app's handler collection and registration descriptors alive.
		  This repro clears RegisteredHandlerServiceTypeSet.s_instances in both scenarios to isolate this from C024.

		Dynamic handler registrations: {Options.RegistrationCount}
		Payload per dynamic view type: {Options.PayloadBytes / 1024 / 1024} MiB
		Payload per dynamic handler type: {Options.PayloadBytes / 1024 / 1024} MiB

		Control: MauiHotReloadHelper.HandlerService cleared after app disposal and before forced GC
		  HotReload HandlerService descriptors before collect: {Control.HotReloadDescriptorCountBeforeCollect}
		  HotReload HandlerService descriptors after collect: {Control.HotReloadDescriptorCountAfterCollect}
		  RegisteredHandlerServiceTypeSet instances before collect: {Control.RegisteredTypeSetInstancesBeforeCollect}
		  RegisteredHandlerServiceTypeSet instances after collect: {Control.RegisteredTypeSetInstancesAfterCollect}
		  Retained assemblies: {Control.RetainedAssemblyCount}
		  Retained view types: {Control.RetainedViewTypeCount}
		  Retained handler types: {Control.RetainedHandlerTypeCount}
		  Retained view payloads: {Control.RetainedViewPayloadCount}
		  Retained handler payloads: {Control.RetainedHandlerPayloadCount}
		  Retained payload bytes: {Control.RetainedPayloadBytes:N0}
		  Managed heap delta: {Control.HeapDeltaBytes:N0} bytes

		Current MAUI: MauiHotReloadHelper.HandlerService left intact after app disposal
		  HotReload HandlerService descriptors before collect: {Current.HotReloadDescriptorCountBeforeCollect}
		  HotReload HandlerService descriptors after collect: {Current.HotReloadDescriptorCountAfterCollect}
		  RegisteredHandlerServiceTypeSet instances before collect: {Current.RegisteredTypeSetInstancesBeforeCollect}
		  RegisteredHandlerServiceTypeSet instances after collect: {Current.RegisteredTypeSetInstancesAfterCollect}
		  Retained assemblies: {Current.RetainedAssemblyCount}
		  Retained view types: {Current.RetainedViewTypeCount}
		  Retained handler types: {Current.RetainedHandlerTypeCount}
		  Retained view payloads: {Current.RetainedViewPayloadCount}
		  Retained handler payloads: {Current.RetainedHandlerPayloadCount}
		  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
		  Managed heap delta: {Current.HeapDeltaBytes:N0} bytes
		""";
}
