using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

var options = ReproOptions.Parse(args);
var probe = new ImageSourceServiceRegistrationTypeRetentionProbe(options);
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

sealed class ImageSourceServiceRegistrationTypeRetentionProbe
{
	readonly ReproOptions _options;

	public ImageSourceServiceRegistrationTypeRetentionProbe(ReproOptions options)
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
		var request = new DynamicImageSourceRegistrationRequest(_options);

		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		using var app = CreateApp(request);
		var services = app.Services.GetRequiredService<IImageSourceServiceCollection>();

		var descriptorCountBeforeCollect = services.Count;
		var mappingCountsBeforeCollect = GetImageSourceMappingCounts(services);

		if (clearRegistrationStateBeforeCollect)
		{
			services.Clear();
			ClearImageSourceMappings(services);
		}

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedImageSourcePayloads = CountAlive(request.ImageSourcePayloadRefs);
		var retainedServicePayloads = CountAlive(request.ServicePayloadRefs);

		return new ScenarioResult(
			DescriptorCountBeforeCollect: descriptorCountBeforeCollect,
			DescriptorCountAfterCollect: services.Count,
			ConcreteMappingEntriesBeforeCollect: mappingCountsBeforeCollect.Concrete,
			ConcreteMappingEntriesAfterCollect: GetImageSourceMappingCounts(services).Concrete,
			InterfaceMappingEntriesBeforeCollect: mappingCountsBeforeCollect.Interface,
			InterfaceMappingEntriesAfterCollect: GetImageSourceMappingCounts(services).Interface,
			RetainedAssemblyCount: CountAlive(request.AssemblyRefs),
			RetainedImageSourceTypeCount: CountAlive(request.ImageSourceTypeRefs),
			RetainedServiceTypeCount: CountAlive(request.ServiceTypeRefs),
			RetainedImageSourcePayloadCount: retainedImageSourcePayloads,
			RetainedServicePayloadCount: retainedServicePayloads,
			RetainedPayloadBytes: (long)(retainedImageSourcePayloads + retainedServicePayloads) * _options.PayloadBytes,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	static MauiApp CreateApp(DynamicImageSourceRegistrationRequest request)
	{
		RegistrationState.Current = request;
		try
		{
			var builder = MauiApp.CreateBuilder(useDefaults: false);
			builder.ConfigureImageSources(static services =>
			{
				var current = RegistrationState.Current
					?? throw new InvalidOperationException("No active image-source registration request.");
				current.Register(services);
			});

			var app = builder.Build();
			_ = app.Services.GetRequiredService<IImageSourceServiceCollection>();
			return app;
		}
		finally
		{
			RegistrationState.Current = null;
		}
	}

	static (int Concrete, int Interface) GetImageSourceMappingCounts(IImageSourceServiceCollection services)
	{
		var instance = GetImageSourceMapping(services);
		return (
			GetDictionaryCount(instance, "_concreteTypeMapping"),
			GetDictionaryCount(instance, "_interfaceTypeMapping"));
	}

	static void ClearImageSourceMappings(IImageSourceServiceCollection services)
	{
		var instance = GetImageSourceMapping(services);
		ClearDictionary(instance, "_concreteTypeMapping");
		ClearDictionary(instance, "_interfaceTypeMapping");
	}

	static object GetImageSourceMapping(IImageSourceServiceCollection services)
	{
		var type = typeof(IImageSourceServiceCollection).Assembly.GetType("Microsoft.Maui.Hosting.ImageSourceToImageSourceServiceTypeMapping")
			?? throw new InvalidOperationException("ImageSourceToImageSourceServiceTypeMapping was not found.");
		var getInstance = type.GetMethod("GetInstance", BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingMethodException(type.FullName, "GetInstance");

		return getInstance.Invoke(null, new object[] { services })
			?? throw new InvalidOperationException("Image source mapping was null.");
	}

	static int GetDictionaryCount(object owner, string fieldName)
	{
		var dictionary = GetDictionary(owner, fieldName);
		return dictionary.GetType().GetProperty("Count")?.GetValue(dictionary) is int count
			? count
			: -1;
	}

	static void ClearDictionary(object owner, string fieldName)
	{
		var dictionary = GetDictionary(owner, fieldName);
		dictionary.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(dictionary, null);
	}

	static object GetDictionary(object owner, string fieldName)
	{
		var field = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(owner.GetType().FullName, fieldName);

		return field.GetValue(owner)
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

sealed class DynamicImageSourceRegistrationRequest
{
	static readonly MethodInfo AddServiceMethod =
		typeof(ImageSourceServiceCollectionExtensions)
			.GetMethods(BindingFlags.Public | BindingFlags.Static)
			.Single(method =>
				method.Name == nameof(ImageSourceServiceCollectionExtensions.AddService) &&
				method.GetGenericArguments().Length == 2 &&
				method.GetParameters().Length == 1);

	readonly ReproOptions _options;

	public DynamicImageSourceRegistrationRequest(ReproOptions options)
	{
		_options = options;
	}

	public List<WeakReference<Assembly>> AssemblyRefs { get; } = new();

	public List<WeakReference<Type>> ImageSourceTypeRefs { get; } = new();

	public List<WeakReference<Type>> ServiceTypeRefs { get; } = new();

	public List<WeakReference<byte[]>> ImageSourcePayloadRefs { get; } = new();

	public List<WeakReference<byte[]>> ServicePayloadRefs { get; } = new();

	[MethodImpl(MethodImplOptions.NoInlining)]
	public void Register(IImageSourceServiceCollection services)
	{
		for (var i = 0; i < _options.RegistrationCount; i++)
		{
			DynamicImageSourceServiceTypeFactory.Create(
				i,
				_options.PayloadBytes,
				out var imageSourceType,
				out var serviceType,
				out var imageSourcePayload,
				out var servicePayload);

			AssemblyRefs.Add(new WeakReference<Assembly>(imageSourceType.Assembly, trackResurrection: false));
			ImageSourceTypeRefs.Add(new WeakReference<Type>(imageSourceType, trackResurrection: false));
			ServiceTypeRefs.Add(new WeakReference<Type>(serviceType, trackResurrection: false));
			ImageSourcePayloadRefs.Add(new WeakReference<byte[]>(imageSourcePayload, trackResurrection: false));
			ServicePayloadRefs.Add(new WeakReference<byte[]>(servicePayload, trackResurrection: false));

			AddServiceMethod
				.MakeGenericMethod(imageSourceType, serviceType)
				.Invoke(null, new object[] { services });
		}
	}
}

static class RegistrationState
{
	public static DynamicImageSourceRegistrationRequest? Current;
}

static class DynamicImageSourceServiceTypeFactory
{
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static void Create(
		int index,
		int payloadBytes,
		out Type imageSourceType,
		out Type serviceType,
		out byte[] imageSourcePayload,
		out byte[] servicePayload)
	{
		var assemblyName = new AssemblyName($"ImageSourceServiceRegistrationRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

		imageSourceType = CreateImageSourceType(moduleBuilder, index);
		serviceType = CreateServiceType(moduleBuilder, index, imageSourceType);

		imageSourcePayload = CreatePayload(index, payloadBytes);
		servicePayload = CreatePayload(index + 113, payloadBytes);

		imageSourceType.GetField("ImageSourcePayload")!.SetValue(null, imageSourcePayload);
		serviceType.GetField("ServicePayload")!.SetValue(null, servicePayload);
	}

	static Type CreateImageSourceType(ModuleBuilder moduleBuilder, int index)
	{
		var typeBuilder = moduleBuilder.DefineType(
			$"PluginImageSource{index}",
			TypeAttributes.Public | TypeAttributes.Class);
		typeBuilder.AddInterfaceImplementation(typeof(IImageSource));
		DefineDefaultConstructor(typeBuilder);

		typeBuilder.DefineField(
			"ImageSourcePayload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

		var isEmptyProperty = typeBuilder.DefineProperty(
			nameof(IImageSource.IsEmpty),
			PropertyAttributes.None,
			typeof(bool),
			Type.EmptyTypes);
		var isEmptyGetter = typeBuilder.DefineMethod(
			"get_" + nameof(IImageSource.IsEmpty),
			MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
			typeof(bool),
			Type.EmptyTypes);
		var isEmptyIl = isEmptyGetter.GetILGenerator();
		isEmptyIl.Emit(OpCodes.Ldc_I4_0);
		isEmptyIl.Emit(OpCodes.Ret);
		isEmptyProperty.SetGetMethod(isEmptyGetter);
		typeBuilder.DefineMethodOverride(isEmptyGetter, typeof(IImageSource).GetProperty(nameof(IImageSource.IsEmpty))!.GetMethod!);

		return typeBuilder.CreateType()!;
	}

	static Type CreateServiceType(ModuleBuilder moduleBuilder, int index, Type imageSourceType)
	{
		var serviceInterface = typeof(IImageSourceService<>).MakeGenericType(imageSourceType);
		var typeBuilder = moduleBuilder.DefineType(
			$"PluginImageSourceService{index}",
			TypeAttributes.Public | TypeAttributes.Class);
		typeBuilder.AddInterfaceImplementation(serviceInterface);
		DefineDefaultConstructor(typeBuilder);

		typeBuilder.DefineField(
			"ServicePayload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

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
	int ConcreteMappingEntriesBeforeCollect,
	int ConcreteMappingEntriesAfterCollect,
	int InterfaceMappingEntriesBeforeCollect,
	int InterfaceMappingEntriesAfterCollect,
	int RetainedAssemblyCount,
	int RetainedImageSourceTypeCount,
	int RetainedServiceTypeCount,
	int RetainedImageSourcePayloadCount,
	int RetainedServicePayloadCount,
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
		Control.RetainedImageSourceTypeCount == 0 &&
		Control.RetainedServiceTypeCount == 0 &&
		Control.RetainedImageSourcePayloadCount == 0 &&
		Control.RetainedServicePayloadCount == 0 &&
		Current.RetainedAssemblyCount == Options.RegistrationCount &&
		Current.RetainedImageSourceTypeCount == Options.RegistrationCount &&
		Current.RetainedServiceTypeCount == Options.RegistrationCount &&
		Current.RetainedImageSourcePayloadCount == Options.RegistrationCount &&
		Current.RetainedServicePayloadCount == Options.RegistrationCount;

	public override string ToString() =>
		$"""
		MAUI image-source service registration type-retention repro
		Result: {(Proven ? "PROVEN" : "NOT PROVEN")}

		Trigger:
		  ConfigureImageSources(...) feeds public IImageSourceServiceCollection.AddService<TImageSource,TService>() registrations into the app-lifetime image-source service collection.
		  AddService stores each image-source Type and image-source-service Type in ImageSourceToImageSourceServiceTypeMapping and MauiServiceCollection service descriptors.
		  There is no public unregister or scoped eviction path for dynamically loaded image-source service registrations while the app-lifetime provider lives.
		  Plugin/module image-source service registrations can therefore stay rooted after the plugin should unload.

		Dynamic image-source service registrations: {Options.RegistrationCount}
		Payload per dynamic image-source type: {Options.PayloadBytes / 1024 / 1024} MiB
		Payload per dynamic image-source-service type: {Options.PayloadBytes / 1024 / 1024} MiB

		Control: IImageSourceServiceCollection descriptors and mappings cleared before forced GC
		  Service descriptors before collect: {Control.DescriptorCountBeforeCollect}
		  Service descriptors after collect: {Control.DescriptorCountAfterCollect}
		  Concrete mappings before collect: {Control.ConcreteMappingEntriesBeforeCollect}
		  Concrete mappings after collect: {Control.ConcreteMappingEntriesAfterCollect}
		  Interface mappings before collect: {Control.InterfaceMappingEntriesBeforeCollect}
		  Interface mappings after collect: {Control.InterfaceMappingEntriesAfterCollect}
		  Retained assemblies: {Control.RetainedAssemblyCount}
		  Retained image-source types: {Control.RetainedImageSourceTypeCount}
		  Retained image-source-service types: {Control.RetainedServiceTypeCount}
		  Retained image-source payloads: {Control.RetainedImageSourcePayloadCount}
		  Retained service payloads: {Control.RetainedServicePayloadCount}
		  Retained payload bytes: {Control.RetainedPayloadBytes:N0}
		  Managed heap delta: {Control.HeapDeltaBytes:N0} bytes

		Current MAUI: IImageSourceServiceCollection registration state left intact
		  Service descriptors before collect: {Current.DescriptorCountBeforeCollect}
		  Service descriptors after collect: {Current.DescriptorCountAfterCollect}
		  Concrete mappings before collect: {Current.ConcreteMappingEntriesBeforeCollect}
		  Concrete mappings after collect: {Current.ConcreteMappingEntriesAfterCollect}
		  Interface mappings before collect: {Current.InterfaceMappingEntriesBeforeCollect}
		  Interface mappings after collect: {Current.InterfaceMappingEntriesAfterCollect}
		  Retained assemblies: {Current.RetainedAssemblyCount}
		  Retained image-source types: {Current.RetainedImageSourceTypeCount}
		  Retained image-source-service types: {Current.RetainedServiceTypeCount}
		  Retained image-source payloads: {Current.RetainedImageSourcePayloadCount}
		  Retained service payloads: {Current.RetainedServicePayloadCount}
		  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
		  Managed heap delta: {Current.HeapDeltaBytes:N0} bytes
		""";
}
