using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

var options = ReproOptions.Parse(args);
var probe = new MauiServiceCollectionFactoryDelegateRetentionProbe(options);
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

sealed class MauiServiceCollectionFactoryDelegateRetentionProbe
{
	static readonly FieldInfo HotReloadHandlerServiceField =
		typeof(MauiApp).Assembly.GetType("Microsoft.Maui.HotReload.MauiHotReloadHelper", throwOnError: true)!
			.GetField("HandlerService", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingFieldException("Microsoft.Maui.HotReload.MauiHotReloadHelper", "HandlerService");

	readonly ReproOptions _options;

	public MauiServiceCollectionFactoryDelegateRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearDynamicFactoryDescriptorsBeforeCollect: true);
		var current = RunScenario(clearDynamicFactoryDescriptorsBeforeCollect: false);

		ClearHotReloadHandlerService();
		CollectHard();

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearDynamicFactoryDescriptorsBeforeCollect)
	{
		ClearHotReloadHandlerService();
		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var request = CreateLiveAppWithDynamicFactories(_options);
		var handlers = request.App.Services.GetRequiredService<IMauiHandlersCollection>();
		var imageServices = request.App.Services.GetRequiredService<IImageSourceServiceCollection>();

		var handlerDescriptorCountBeforeCollect = handlers.Count;
		var imageServiceDescriptorCountBeforeCollect = imageServices.Count;
		var handlerFactoryDelegatesBeforeCollect = CountDynamicFactoryDelegates(handlers);
		var imageServiceFactoryDelegatesBeforeCollect = CountDynamicFactoryDelegates(imageServices);

		if (clearDynamicFactoryDescriptorsBeforeCollect)
		{
			RemoveDynamicFactoryDescriptors(handlers);
			RemoveDynamicFactoryDescriptors(imageServices);
		}

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedHandlerPayloads = CountAlive(request.HandlerFactoryPayloadRefs);
		var retainedImageServicePayloads = CountAlive(request.ImageServiceFactoryPayloadRefs);
		var result = new ScenarioResult(
			HandlerDescriptorCountBeforeCollect: handlerDescriptorCountBeforeCollect,
			HandlerDescriptorCountAfterCollect: handlers.Count,
			ImageServiceDescriptorCountBeforeCollect: imageServiceDescriptorCountBeforeCollect,
			ImageServiceDescriptorCountAfterCollect: imageServices.Count,
			HandlerFactoryDelegatesBeforeCollect: handlerFactoryDelegatesBeforeCollect,
			HandlerFactoryDelegatesAfterCollect: CountDynamicFactoryDelegates(handlers),
			ImageServiceFactoryDelegatesBeforeCollect: imageServiceFactoryDelegatesBeforeCollect,
			ImageServiceFactoryDelegatesAfterCollect: CountDynamicFactoryDelegates(imageServices),
			RetainedHandlerFactoryAssemblyCount: CountAlive(request.HandlerFactoryAssemblyRefs),
			RetainedHandlerFactoryTypeCount: CountAlive(request.HandlerFactoryTypeRefs),
			RetainedHandlerFactoryInstanceCount: CountAlive(request.HandlerFactoryInstanceRefs),
			RetainedHandlerFactoryPayloadCount: retainedHandlerPayloads,
			RetainedImageServiceFactoryAssemblyCount: CountAlive(request.ImageServiceFactoryAssemblyRefs),
			RetainedImageServiceFactoryTypeCount: CountAlive(request.ImageServiceFactoryTypeRefs),
			RetainedImageServiceFactoryInstanceCount: CountAlive(request.ImageServiceFactoryInstanceRefs),
			RetainedImageServiceFactoryPayloadCount: retainedImageServicePayloads,
			RetainedPayloadBytes: (long)(retainedHandlerPayloads + retainedImageServicePayloads) * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);

		request.Dispose();
		ClearHotReloadHandlerService();
		CollectHard();

		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static DynamicFactoryRegistrationRequest CreateLiveAppWithDynamicFactories(ReproOptions options)
	{
		var request = new DynamicFactoryRegistrationRequest(options);
		RegistrationState.Current = request;
		try
		{
			var builder = MauiApp.CreateBuilder(useDefaults: false);
			builder.ConfigureMauiHandlers(static handlers =>
			{
				var current = RegistrationState.Current
					?? throw new InvalidOperationException("No active factory registration request.");
				current.RegisterHandlerFactories(handlers);
			});
			builder.ConfigureImageSources(static imageServices =>
			{
				var current = RegistrationState.Current
					?? throw new InvalidOperationException("No active factory registration request.");
				current.RegisterImageServiceFactories(imageServices);
			});

			request.App = builder.Build();
			_ = request.App.Services.GetRequiredService<IMauiHandlersCollection>();
			_ = request.App.Services.GetRequiredService<IImageSourceServiceCollection>();
			return request;
		}
		finally
		{
			RegistrationState.Current = null;
		}
	}

	static int CountDynamicFactoryDelegates(IEnumerable<ServiceDescriptor> descriptors)
	{
		var count = 0;
		foreach (var descriptor in descriptors)
		{
			if (descriptor.ImplementationFactory is { } factory)
				count += CountDynamicDelegates(factory);
		}

		return count;
	}

	static void RemoveDynamicFactoryDescriptors(ICollection<ServiceDescriptor> descriptors)
	{
		var dynamicDescriptors = descriptors
			.Where(descriptor => descriptor.ImplementationFactory is { } factory && CountDynamicDelegates(factory) > 0)
			.ToArray();

		foreach (var descriptor in dynamicDescriptors)
			descriptors.Remove(descriptor);
	}

	static int CountDynamicDelegates(Delegate root)
	{
		var visitedObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
		var visitedDelegates = new HashSet<Delegate>(ReferenceEqualityComparer.Instance);
		return CountDynamicDelegates(root, visitedObjects, visitedDelegates, depth: 0);
	}

	static int CountDynamicDelegates(
		Delegate root,
		HashSet<object> visitedObjects,
		HashSet<Delegate> visitedDelegates,
		int depth)
	{
		if (!visitedDelegates.Add(root) || depth > 5)
			return 0;

		var count = IsDynamicFactoryDelegate(root) ? 1 : 0;
		foreach (var item in root.GetInvocationList())
		{
			if (!ReferenceEquals(item, root))
				count += CountDynamicDelegates(item, visitedObjects, visitedDelegates, depth + 1);
		}

		if (root.Target is { } target)
			count += CountDynamicDelegatesInObject(target, visitedObjects, visitedDelegates, depth + 1);

		return count;
	}

	static int CountDynamicDelegatesInObject(
		object value,
		HashSet<object> visitedObjects,
		HashSet<Delegate> visitedDelegates,
		int depth)
	{
		if (value is string || value.GetType().IsPrimitive || !visitedObjects.Add(value) || depth > 5)
			return 0;

		var count = 0;
		foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
		{
			if (field.GetValue(value) is Delegate nestedDelegate)
			{
				count += CountDynamicDelegates(nestedDelegate, visitedObjects, visitedDelegates, depth + 1);
			}
		}

		return count;
	}

	static bool IsDynamicFactoryDelegate(Delegate del) =>
		del.Method.DeclaringType?.Assembly.GetName().Name?.StartsWith(DynamicFactoryTargetFactory.DynamicAssemblyPrefix, StringComparison.Ordinal) == true;

	static void ClearHotReloadHandlerService() =>
		HotReloadHandlerServiceField.SetValue(null, null);

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

sealed class DynamicFactoryRegistrationRequest : IDisposable
{
	readonly ReproOptions _options;

	public DynamicFactoryRegistrationRequest(ReproOptions options)
	{
		_options = options;
	}

	public MauiApp App { get; set; } = null!;

	public List<WeakReference<Assembly>> HandlerFactoryAssemblyRefs { get; } = new();

	public List<WeakReference<Type>> HandlerFactoryTypeRefs { get; } = new();

	public List<WeakReference<object>> HandlerFactoryInstanceRefs { get; } = new();

	public List<WeakReference<byte[]>> HandlerFactoryPayloadRefs { get; } = new();

	public List<WeakReference<Assembly>> ImageServiceFactoryAssemblyRefs { get; } = new();

	public List<WeakReference<Type>> ImageServiceFactoryTypeRefs { get; } = new();

	public List<WeakReference<object>> ImageServiceFactoryInstanceRefs { get; } = new();

	public List<WeakReference<byte[]>> ImageServiceFactoryPayloadRefs { get; } = new();

	[MethodImpl(MethodImplOptions.NoInlining)]
	public void RegisterHandlerFactories(IMauiHandlersCollection handlers)
	{
		for (var i = 0; i < _options.RegistrationCount; i++)
		{
			var target = DynamicFactoryTargetFactory.CreateHandlerFactoryTarget(
				i,
				_options.PayloadBytes,
				out var targetType,
				out var payload);

			var factory = (Func<IServiceProvider, IElementHandler>)DynamicFactoryTargetFactory.CreateDelegate(
				target,
				targetType,
				"CreateHandler",
				typeof(Func<IServiceProvider, IElementHandler>));

			handlers.AddHandler<StaticElement>(factory);
			TrackHandlerFactory(targetType, target, payload);
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	public void RegisterImageServiceFactories(IImageSourceServiceCollection imageServices)
	{
		for (var i = 0; i < _options.RegistrationCount; i++)
		{
			var target = DynamicFactoryTargetFactory.CreateImageServiceFactoryTarget(
				i,
				_options.PayloadBytes,
				out var targetType,
				out var payload);

			var factory = (Func<IServiceProvider, IImageSourceService<StaticImageSource>>)DynamicFactoryTargetFactory.CreateDelegate(
				target,
				targetType,
				"CreateImageSourceService",
				typeof(Func<IServiceProvider, IImageSourceService<StaticImageSource>>));

			imageServices.AddService<StaticImageSource>(factory);
			TrackImageServiceFactory(targetType, target, payload);
		}
	}

	void TrackHandlerFactory(Type targetType, object targetInstance, byte[] payload)
	{
		HandlerFactoryAssemblyRefs.Add(new WeakReference<Assembly>(targetType.Assembly, trackResurrection: false));
		HandlerFactoryTypeRefs.Add(new WeakReference<Type>(targetType, trackResurrection: false));
		HandlerFactoryInstanceRefs.Add(new WeakReference<object>(targetInstance, trackResurrection: false));
		HandlerFactoryPayloadRefs.Add(new WeakReference<byte[]>(payload, trackResurrection: false));
	}

	void TrackImageServiceFactory(Type targetType, object targetInstance, byte[] payload)
	{
		ImageServiceFactoryAssemblyRefs.Add(new WeakReference<Assembly>(targetType.Assembly, trackResurrection: false));
		ImageServiceFactoryTypeRefs.Add(new WeakReference<Type>(targetType, trackResurrection: false));
		ImageServiceFactoryInstanceRefs.Add(new WeakReference<object>(targetInstance, trackResurrection: false));
		ImageServiceFactoryPayloadRefs.Add(new WeakReference<byte[]>(payload, trackResurrection: false));
	}

	public void Dispose()
	{
		App.Dispose();
	}
}

static class RegistrationState
{
	public static DynamicFactoryRegistrationRequest? Current;
}

static class DynamicFactoryTargetFactory
{
	public const string DynamicAssemblyPrefix = "MauiServiceCollectionFactoryDelegateRetentionReproDynamic";

	[MethodImpl(MethodImplOptions.NoInlining)]
	public static object CreateHandlerFactoryTarget(int index, int payloadBytes, out Type targetType, out byte[] payload)
	{
		targetType = CreateTargetType(
			$"{DynamicAssemblyPrefix}Handler{index}",
			$"PluginHandlerFactoryTarget{index}",
			"CreateHandler",
			typeof(IElementHandler));
		payload = CreatePayload(payloadBytes, index);
		return Activator.CreateInstance(targetType, payload)
			?? throw new InvalidOperationException("Failed to create handler factory target.");
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	public static object CreateImageServiceFactoryTarget(int index, int payloadBytes, out Type targetType, out byte[] payload)
	{
		targetType = CreateTargetType(
			$"{DynamicAssemblyPrefix}Image{index}",
			$"PluginImageServiceFactoryTarget{index}",
			"CreateImageSourceService",
			typeof(IImageSourceService<StaticImageSource>));
		payload = CreatePayload(payloadBytes, index + 101);
		return Activator.CreateInstance(targetType, payload)
			?? throw new InvalidOperationException("Failed to create image-service factory target.");
	}

	public static Delegate CreateDelegate(object target, Type targetType, string methodName, Type delegateType)
	{
		var method = targetType.GetMethod(methodName)
			?? throw new MissingMethodException(targetType.FullName, methodName);
		return Delegate.CreateDelegate(delegateType, target, method);
	}

	static Type CreateTargetType(string assemblyNameValue, string typeName, string factoryMethodName, Type returnType)
	{
		var assemblyName = new AssemblyName(assemblyNameValue);
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");
		var typeBuilder = moduleBuilder.DefineType(
			typeName,
			TypeAttributes.Public | TypeAttributes.Class);

		var payloadField = typeBuilder.DefineField("_payload", typeof(byte[]), FieldAttributes.Private);
		DefineConstructor(typeBuilder, payloadField);
		DefineFactoryMethod(typeBuilder, payloadField, factoryMethodName, returnType);

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

	static void DefineFactoryMethod(TypeBuilder typeBuilder, FieldInfo payloadField, string methodName, Type returnType)
	{
		var method = typeBuilder.DefineMethod(
			methodName,
			MethodAttributes.Public | MethodAttributes.HideBySig,
			returnType,
			new[] { typeof(IServiceProvider) });

		var il = method.GetILGenerator();
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Ldfld, payloadField);
		il.Emit(OpCodes.Pop);
		il.Emit(OpCodes.Ldnull);
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

public sealed class StaticElement : IElement
{
	public IElementHandler? Handler { get; set; }

	public IElement? Parent => null;
}

public sealed class StaticImageSource : IImageSource
{
	public bool IsEmpty => false;
}

sealed record ScenarioResult(
	int HandlerDescriptorCountBeforeCollect,
	int HandlerDescriptorCountAfterCollect,
	int ImageServiceDescriptorCountBeforeCollect,
	int ImageServiceDescriptorCountAfterCollect,
	int HandlerFactoryDelegatesBeforeCollect,
	int HandlerFactoryDelegatesAfterCollect,
	int ImageServiceFactoryDelegatesBeforeCollect,
	int ImageServiceFactoryDelegatesAfterCollect,
	int RetainedHandlerFactoryAssemblyCount,
	int RetainedHandlerFactoryTypeCount,
	int RetainedHandlerFactoryInstanceCount,
	int RetainedHandlerFactoryPayloadCount,
	int RetainedImageServiceFactoryAssemblyCount,
	int RetainedImageServiceFactoryTypeCount,
	int RetainedImageServiceFactoryInstanceCount,
	int RetainedImageServiceFactoryPayloadCount,
	long RetainedPayloadBytes,
	long HeapBeforeBytes,
	long HeapAfterBytes)
{
	public long HeapDeltaBytes => HeapAfterBytes - HeapBeforeBytes;
}

sealed record ReproReport(ReproOptions Options, ScenarioResult Control, ScenarioResult Current)
{
	public bool Proven =>
		Control.HandlerFactoryDelegatesAfterCollect == 0 &&
		Control.ImageServiceFactoryDelegatesAfterCollect == 0 &&
		Control.RetainedHandlerFactoryPayloadCount == 0 &&
		Control.RetainedImageServiceFactoryPayloadCount == 0 &&
		Current.HandlerFactoryDelegatesAfterCollect == Options.RegistrationCount &&
		Current.ImageServiceFactoryDelegatesAfterCollect == Options.RegistrationCount &&
		Current.RetainedHandlerFactoryAssemblyCount == Options.RegistrationCount &&
		Current.RetainedHandlerFactoryTypeCount == Options.RegistrationCount &&
		Current.RetainedHandlerFactoryInstanceCount == Options.RegistrationCount &&
		Current.RetainedHandlerFactoryPayloadCount == Options.RegistrationCount &&
		Current.RetainedImageServiceFactoryAssemblyCount == Options.RegistrationCount &&
		Current.RetainedImageServiceFactoryTypeCount == Options.RegistrationCount &&
		Current.RetainedImageServiceFactoryInstanceCount == Options.RegistrationCount &&
		Current.RetainedImageServiceFactoryPayloadCount == Options.RegistrationCount;

	public override string ToString()
	{
		return $"""
			MAUI service collection factory delegate retention repro
			Result: {(Proven ? "PROVEN" : "NOT PROVEN")}

			Trigger:
			  AddHandler<T>(Func<IServiceProvider,IElementHandler>) and AddService<TImageSource>(Func<IServiceProvider,IImageSourceService<TImageSource>>) store user factories in live MauiServiceCollection descriptors.
			  This repro uses static registered element and image-source types, so dynamic type-registration metadata is not required for the retained graph.
			  The control removes only descriptors whose ImplementationFactory contains a dynamic factory delegate while the app/provider and static registration metadata remain live.

			Dynamic handler factory targets: {Options.RegistrationCount}
			Dynamic image-service factory targets: {Options.RegistrationCount}
			Payload per factory target: {Options.PayloadMib} MiB

			Control: dynamic factory descriptors removed before forced GC while the app remains live
			  Handler descriptors before collect: {Control.HandlerDescriptorCountBeforeCollect}
			  Handler descriptors after collect: {Control.HandlerDescriptorCountAfterCollect}
			  Image-service descriptors before collect: {Control.ImageServiceDescriptorCountBeforeCollect}
			  Image-service descriptors after collect: {Control.ImageServiceDescriptorCountAfterCollect}
			  Dynamic handler factory delegates after collect: {Control.HandlerFactoryDelegatesAfterCollect}
			  Dynamic image-service factory delegates after collect: {Control.ImageServiceFactoryDelegatesAfterCollect}
			  Retained handler factory assemblies: {Control.RetainedHandlerFactoryAssemblyCount}/{Options.RegistrationCount}
			  Retained handler factory target types: {Control.RetainedHandlerFactoryTypeCount}/{Options.RegistrationCount}
			  Retained handler factory target instances: {Control.RetainedHandlerFactoryInstanceCount}/{Options.RegistrationCount}
			  Retained handler factory payloads: {Control.RetainedHandlerFactoryPayloadCount}/{Options.RegistrationCount}
			  Retained image-service factory assemblies: {Control.RetainedImageServiceFactoryAssemblyCount}/{Options.RegistrationCount}
			  Retained image-service factory target types: {Control.RetainedImageServiceFactoryTypeCount}/{Options.RegistrationCount}
			  Retained image-service factory target instances: {Control.RetainedImageServiceFactoryInstanceCount}/{Options.RegistrationCount}
			  Retained image-service factory payloads: {Control.RetainedImageServiceFactoryPayloadCount}/{Options.RegistrationCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}
			  Managed heap delta: {Control.HeapDeltaBytes:N0} bytes

			Current MAUI: dynamic factory descriptors left intact while the app remains live
			  Handler descriptors before collect: {Current.HandlerDescriptorCountBeforeCollect}
			  Handler descriptors after collect: {Current.HandlerDescriptorCountAfterCollect}
			  Image-service descriptors before collect: {Current.ImageServiceDescriptorCountBeforeCollect}
			  Image-service descriptors after collect: {Current.ImageServiceDescriptorCountAfterCollect}
			  Dynamic handler factory delegates after collect: {Current.HandlerFactoryDelegatesAfterCollect}
			  Dynamic image-service factory delegates after collect: {Current.ImageServiceFactoryDelegatesAfterCollect}
			  Retained handler factory assemblies: {Current.RetainedHandlerFactoryAssemblyCount}/{Options.RegistrationCount}
			  Retained handler factory target types: {Current.RetainedHandlerFactoryTypeCount}/{Options.RegistrationCount}
			  Retained handler factory target instances: {Current.RetainedHandlerFactoryInstanceCount}/{Options.RegistrationCount}
			  Retained handler factory payloads: {Current.RetainedHandlerFactoryPayloadCount}/{Options.RegistrationCount}
			  Retained image-service factory assemblies: {Current.RetainedImageServiceFactoryAssemblyCount}/{Options.RegistrationCount}
			  Retained image-service factory target types: {Current.RetainedImageServiceFactoryTypeCount}/{Options.RegistrationCount}
			  Retained image-service factory target instances: {Current.RetainedImageServiceFactoryInstanceCount}/{Options.RegistrationCount}
			  Retained image-service factory payloads: {Current.RetainedImageServiceFactoryPayloadCount}/{Options.RegistrationCount}
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
