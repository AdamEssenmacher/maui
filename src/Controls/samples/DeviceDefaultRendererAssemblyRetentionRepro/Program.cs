using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;

var options = ReproOptions.Parse(args);
var probe = new DeviceDefaultRendererAssemblyRetentionProbe(options);
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

sealed class DeviceDefaultRendererAssemblyRetentionProbe
{
	readonly ReproOptions _options;

	public DeviceDefaultRendererAssemblyRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearDefaultRendererAssemblyBeforeCollect: true);
		var current = RunScenario(clearDefaultRendererAssemblyBeforeCollect: false);

		SetDefaultRendererAssembly(null);
		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearDefaultRendererAssemblyBeforeCollect)
	{
		SetDefaultRendererAssembly(null);

		WeakReference<Assembly> assemblyRef;
		WeakReference<Type> typeRef;
		WeakReference<byte[]> payloadRef;

		CollectHard();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		CreateDynamicRendererAssemblyAndAssign(out assemblyRef, out typeRef, out payloadRef);

		var hadDefaultBeforeCollect = HasDefaultRendererAssembly();

		if (clearDefaultRendererAssemblyBeforeCollect)
			SetDefaultRendererAssembly(null);

		CollectHard();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var retainedPayload = payloadRef.TryGetTarget(out _);
		return new ScenarioResult(
			HadDefaultBeforeCollect: hadDefaultBeforeCollect,
			HasDefaultAfterCollect: HasDefaultRendererAssembly(),
			RetainedAssembly: assemblyRef.TryGetTarget(out _),
			RetainedType: typeRef.TryGetTarget(out _),
			RetainedPayload: retainedPayload,
			RetainedPayloadBytes: retainedPayload ? _options.PayloadBytes : 0,
			HeapBeforeBytes: heapBefore,
			HeapAfterBytes: heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	void CreateDynamicRendererAssemblyAndAssign(
		out WeakReference<Assembly> assemblyRef,
		out WeakReference<Type> typeRef,
		out WeakReference<byte[]> payloadRef)
	{
		var assemblyName = new AssemblyName("MauiDeviceDefaultRendererAssemblyRetentionRepro");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");

		var typeBuilder = moduleBuilder.DefineType(
			"TenantDefaultRendererPack",
			TypeAttributes.Public | TypeAttributes.Class);
		typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);
		var payloadField = typeBuilder.DefineField(
			"Payload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

		var type = typeBuilder.CreateType()!;
		var payload = new byte[_options.PayloadBytes];
		for (var offset = 0; offset < payload.Length; offset += 4096)
			payload[offset] = (byte)(offset % 251);

		type.GetField(payloadField.Name)!.SetValue(null, payload);

		assemblyRef = new WeakReference<Assembly>(type.Assembly, trackResurrection: false);
		typeRef = new WeakReference<Type>(type, trackResurrection: false);
		payloadRef = new WeakReference<byte[]>(payload, trackResurrection: false);

		SetDefaultRendererAssembly(type.Assembly);
	}

	static bool HasDefaultRendererAssembly()
	{
#pragma warning disable CS0612
		return Device.DefaultRendererAssembly is not null;
#pragma warning restore CS0612
	}

	static void SetDefaultRendererAssembly(Assembly? assembly)
	{
#pragma warning disable CS0612
		Device.DefaultRendererAssembly = assembly;
#pragma warning restore CS0612
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
}

sealed record ReproOptions(int PayloadBytes, string? ResultsPath)
{
	public static ReproOptions Parse(string[] args)
	{
		var payloadMiB = 80;
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
	bool HadDefaultBeforeCollect,
	bool HasDefaultAfterCollect,
	bool RetainedAssembly,
	bool RetainedType,
	bool RetainedPayload,
	int RetainedPayloadBytes,
	long HeapBeforeBytes,
	long HeapAfterBytes)
{
	public long HeapDeltaBytes => HeapAfterBytes - HeapBeforeBytes;
}

sealed record ReproReport(ReproOptions Options, ScenarioResult Control, ScenarioResult Current)
{
	public bool Proven =>
		!Control.HasDefaultAfterCollect
		&& !Control.RetainedAssembly
		&& !Control.RetainedType
		&& !Control.RetainedPayload
		&& Current.HasDefaultAfterCollect
		&& Current.RetainedAssembly
		&& Current.RetainedType
		&& Current.RetainedPayload;

	public override string ToString()
	{
		return $"""
			Device.DefaultRendererAssembly retention repro
			Result: {(Proven ? "PROVEN" : "NOT PROVEN")}

			Trigger:
			  Device.DefaultRendererAssembly is an obsolete public static Assembly property used by compatibility renderer registration.
			  Platform compatibility Forms.Init paths set it to the platform renderer assembly, and plugin hosts can set it directly.
			  It is a single-slot root with no scoped registration or dispose/unregister API.

			Dynamic renderer assemblies: 1
			Payload in assembly: {Options.PayloadBytes / 1024 / 1024} MiB

			Control: Device.DefaultRendererAssembly cleared before forced GC
			  Had default renderer assembly before collect: {Control.HadDefaultBeforeCollect}
			  Has default renderer assembly after collect: {Control.HasDefaultAfterCollect}
			  Retained assembly: {Control.RetainedAssembly}
			  Retained type: {Control.RetainedType}
			  Retained payload: {Control.RetainedPayload}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}
			  Managed heap delta: {Control.HeapDeltaBytes:N0} bytes

			Current MAUI: Device.DefaultRendererAssembly left intact
			  Had default renderer assembly before collect: {Current.HadDefaultBeforeCollect}
			  Has default renderer assembly after collect: {Current.HasDefaultAfterCollect}
			  Retained assembly: {Current.RetainedAssembly}
			  Retained type: {Current.RetainedType}
			  Retained payload: {Current.RetainedPayload}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			  Managed heap delta: {Current.HeapDeltaBytes:N0} bytes
			""";
	}
}
