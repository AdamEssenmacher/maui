using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

var options = ReproOptions.Parse(args);
var probe = new MauiHandlersRegistrationTypeRetentionProbe(options);
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

sealed class MauiHandlersRegistrationTypeRetentionProbe
{
	readonly ReproOptions _options;

	public MauiHandlersRegistrationTypeRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearRegistrationStateBeforeCollect: true);
		var current = RunScenario(clearRegistrationStateBeforeCollect: false);

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearRegistrationStateBeforeCollect)
	{
		var request = new DynamicHandlerRegistrationRequest(_options);

		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		using var app = CreateApp(request);
		var handlers = app.Services.GetRequiredService<IMauiHandlersCollection>();

		var descriptorCountBeforeCollect = handlers.Count;
		var registeredTypeCountsBeforeCollect = GetRegisteredHandlerServiceTypeCounts(handlers);

		if (clearRegistrationStateBeforeCollect)
		{
			handlers.Clear();
			ClearRegisteredHandlerServiceTypes(handlers);
		}

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedViewPayloads = CountAlive(request.ViewPayloadRefs);
		var retainedHandlerPayloads = CountAlive(request.HandlerPayloadRefs);

		return new ScenarioResult(
			DescriptorCountBeforeCollect: descriptorCountBeforeCollect,
			DescriptorCountAfterCollect: handlers.Count,
			ConcreteRegistrationEntriesBeforeCollect: registeredTypeCountsBeforeCollect.Concrete,
			ConcreteRegistrationEntriesAfterCollect: GetRegisteredHandlerServiceTypeCounts(handlers).Concrete,
			InterfaceRegistrationEntriesBeforeCollect: registeredTypeCountsBeforeCollect.Interface,
			InterfaceRegistrationEntriesAfterCollect: GetRegisteredHandlerServiceTypeCounts(handlers).Interface,
			RetainedAssemblyCount: CountAlive(request.AssemblyRefs),
			RetainedViewTypeCount: CountAlive(request.ViewTypeRefs),
			RetainedHandlerTypeCount: CountAlive(request.HandlerTypeRefs),
			RetainedViewPayloadCount: retainedViewPayloads,
			RetainedHandlerPayloadCount: retainedHandlerPayloads,
			RetainedPayloadBytes: (long)(retainedViewPayloads + retainedHandlerPayloads) * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	static MauiApp CreateApp(DynamicHandlerRegistrationRequest request)
	{
		RegistrationState.Current = request;
		try
		{
			var builder = MauiApp.CreateBuilder();
			builder.ConfigureMauiHandlers(static handlers =>
			{
				var current = RegistrationState.Current
					?? throw new InvalidOperationException("No active handler registration request.");
				current.Register(handlers);
			});

			var app = builder.Build();
			_ = app.Services.GetRequiredService<IMauiHandlersCollection>();
			return app;
		}
		finally
		{
			RegistrationState.Current = null;
		}
	}

	static (int Concrete, int Interface) GetRegisteredHandlerServiceTypeCounts(IMauiHandlersCollection handlers)
	{
		var instance = GetRegisteredHandlerServiceTypeSet(handlers);
		return (
			GetSetCount(instance, "_concreteHandlerServiceTypeSet"),
			GetSetCount(instance, "_interfaceHandlerServiceTypeSet"));
	}

	static void ClearRegisteredHandlerServiceTypes(IMauiHandlersCollection handlers)
	{
		var instance = GetRegisteredHandlerServiceTypeSet(handlers);
		ClearSet(instance, "_concreteHandlerServiceTypeSet");
		ClearSet(instance, "_interfaceHandlerServiceTypeSet");
	}

	static object GetRegisteredHandlerServiceTypeSet(IMauiHandlersCollection handlers)
	{
		var type = typeof(IMauiHandlersCollection).Assembly.GetType("Microsoft.Maui.Hosting.Internal.RegisteredHandlerServiceTypeSet")
			?? throw new InvalidOperationException("RegisteredHandlerServiceTypeSet was not found.");
		var getInstance = type.GetMethod("GetInstance", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingMethodException(type.FullName, "GetInstance");

		return getInstance.Invoke(null, new object[] { handlers })
			?? throw new InvalidOperationException("Registered handler service type set was null.");
	}

	static int GetSetCount(object setOwner, string fieldName)
	{
		var set = GetSet(setOwner, fieldName);
		return set.GetType().GetProperty("Count")?.GetValue(set) is int count
			? count
			: -1;
	}

	static void ClearSet(object setOwner, string fieldName)
	{
		var set = GetSet(setOwner, fieldName);
		set.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(set, null);
	}

	static object GetSet(object setOwner, string fieldName)
	{
		var field = setOwner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(setOwner.GetType().FullName, fieldName);

		return field.GetValue(setOwner)
			?? throw new InvalidOperationException($"{fieldName} was null.");
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
		var assemblyName = new AssemblyName($"MauiHandlersRegistrationRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

		viewType = CreateViewType(moduleBuilder, index);
		handlerType = CreateHandlerType(moduleBuilder, index);

		viewPayload = CreatePayload(index, payloadBytes);
		handlerPayload = CreatePayload(index + 97, payloadBytes);

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
	int DescriptorCountBeforeCollect,
	int DescriptorCountAfterCollect,
	int ConcreteRegistrationEntriesBeforeCollect,
	int ConcreteRegistrationEntriesAfterCollect,
	int InterfaceRegistrationEntriesBeforeCollect,
	int InterfaceRegistrationEntriesAfterCollect,
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
		MAUI handler registration type-retention repro
		Result: {(Proven ? "PROVEN" : "NOT PROVEN")}

		Trigger:
		  ConfigureMauiHandlers(...) feeds public IMauiHandlersCollection.AddHandler(Type, Type) registrations into the app-lifetime handler collection.
		  AddHandler stores each virtual-view Type in RegisteredHandlerServiceTypeSet and each view/handler Type pair in MauiServiceCollection service descriptors.
		  There is no public unregister or scoped eviction path for dynamically loaded handler registrations while the app-lifetime provider lives.
		  Plugin/module handler registrations can therefore stay rooted after the plugin should unload.

		Dynamic handler registrations: {Options.RegistrationCount}
		Payload per dynamic view type: {Options.PayloadBytes / 1024 / 1024} MiB
		Payload per dynamic handler type: {Options.PayloadBytes / 1024 / 1024} MiB

		Control: IMauiHandlersCollection descriptors and registered type sets cleared before forced GC
		  Service descriptors before collect: {Control.DescriptorCountBeforeCollect}
		  Service descriptors after collect: {Control.DescriptorCountAfterCollect}
		  Concrete registered view types before collect: {Control.ConcreteRegistrationEntriesBeforeCollect}
		  Concrete registered view types after collect: {Control.ConcreteRegistrationEntriesAfterCollect}
		  Interface registered view types before collect: {Control.InterfaceRegistrationEntriesBeforeCollect}
		  Interface registered view types after collect: {Control.InterfaceRegistrationEntriesAfterCollect}
		  Retained assemblies: {Control.RetainedAssemblyCount}
		  Retained view types: {Control.RetainedViewTypeCount}
		  Retained handler types: {Control.RetainedHandlerTypeCount}
		  Retained view payloads: {Control.RetainedViewPayloadCount}
		  Retained handler payloads: {Control.RetainedHandlerPayloadCount}
		  Retained payload bytes: {Control.RetainedPayloadBytes:N0}
		  Managed heap delta: {Control.HeapDeltaBytes:N0} bytes

		Current MAUI: IMauiHandlersCollection registration state left intact
		  Service descriptors before collect: {Current.DescriptorCountBeforeCollect}
		  Service descriptors after collect: {Current.DescriptorCountAfterCollect}
		  Concrete registered view types before collect: {Current.ConcreteRegistrationEntriesBeforeCollect}
		  Concrete registered view types after collect: {Current.ConcreteRegistrationEntriesAfterCollect}
		  Interface registered view types before collect: {Current.InterfaceRegistrationEntriesBeforeCollect}
		  Interface registered view types after collect: {Current.InterfaceRegistrationEntriesAfterCollect}
		  Retained assemblies: {Current.RetainedAssemblyCount}
		  Retained view types: {Current.RetainedViewTypeCount}
		  Retained handler types: {Current.RetainedHandlerTypeCount}
		  Retained view payloads: {Current.RetainedViewPayloadCount}
		  Retained handler payloads: {Current.RetainedHandlerPayloadCount}
		  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
		  Managed heap delta: {Current.HeapDeltaBytes:N0} bytes
		""";
}
