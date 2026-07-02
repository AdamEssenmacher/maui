using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;
using MauiLabel = Microsoft.Maui.Controls.Label;

var options = ReproOptions.Parse(args);
var probe = new BindingExpressionMethodInfoRetentionProbe(options);
var report = probe.Run();

Console.WriteLine(report);

if (!string.IsNullOrWhiteSpace(options.ResultsPath))
{
	var resultsDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ResultsPath));
	if (!string.IsNullOrEmpty(resultsDirectory))
		Directory.CreateDirectory(resultsDirectory);

	File.WriteAllText(options.ResultsPath, report.ToString());
}

return report.Current.RetainedAssemblyCount == options.TypeCount
	&& report.Current.RetainedTypeCount == options.TypeCount
	&& report.Current.RetainedSourceCount == 0
	&& report.Current.RetainedPayloadCount == options.TypeCount
	&& report.Control.RetainedAssemblyCount == 0
	&& report.Control.RetainedTypeCount == 0
	&& report.Control.RetainedSourceCount == 0
	&& report.Control.RetainedPayloadCount == 0
	? 0
	: 1;

sealed class BindingExpressionMethodInfoRetentionProbe
{
	readonly ReproOptions _options;

	public BindingExpressionMethodInfoRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(removeBindingBeforeFinalCollect: true);
		var current = RunScenario(removeBindingBeforeFinalCollect: false);

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool removeBindingBeforeFinalCollect)
	{
		var liveTargets = new List<MauiLabel>(_options.TypeCount);
		var assemblyRefs = new List<WeakReference>(_options.TypeCount);
		var typeRefs = new List<WeakReference>(_options.TypeCount);
		var sourceRefs = new List<WeakReference>(_options.TypeCount);
		var payloadRefs = new List<WeakReference>(_options.TypeCount);

		for (var i = 0; i < _options.TypeCount; i++)
			CreateBoundTarget(i, liveTargets, assemblyRefs, typeRefs, sourceRefs, payloadRefs);

		foreach (var label in liveTargets)
		{
			label.BindingContext = null;

			if (removeBindingBeforeFinalCollect)
				label.RemoveBinding(MauiLabel.TextProperty);
		}

		CollectHard();

		var retainedPayloads = payloadRefs.Count(static wr => wr.IsAlive);

		return new ScenarioResult(
			LiveTargetCount: liveTargets.Count,
			RetainedAssemblyCount: assemblyRefs.Count(static wr => wr.IsAlive),
			RetainedTypeCount: typeRefs.Count(static wr => wr.IsAlive),
			RetainedSourceCount: sourceRefs.Count(static wr => wr.IsAlive),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	void CreateBoundTarget(
		int index,
		List<MauiLabel> liveTargets,
		List<WeakReference> assemblyRefs,
		List<WeakReference> typeRefs,
		List<WeakReference> sourceRefs,
		List<WeakReference> payloadRefs)
	{
		var assemblyName = new AssemblyName($"BindingExpressionMethodInfoRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");
		var typeBuilder = moduleBuilder.DefineType(
			$"DynamicBindingSource{index}",
			TypeAttributes.Public | TypeAttributes.Class);

		var payloadField = typeBuilder.DefineField(
			"Payload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

		typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);

		var propertyBuilder = typeBuilder.DefineProperty(
			"Value",
			PropertyAttributes.None,
			typeof(string),
			Type.EmptyTypes);

		var getMethod = typeBuilder.DefineMethod(
			"get_Value",
			MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
			typeof(string),
			Type.EmptyTypes);

		var il = getMethod.GetILGenerator();
		il.Emit(OpCodes.Ldstr, "Bound value");
		il.Emit(OpCodes.Ret);
		propertyBuilder.SetGetMethod(getMethod);

		var type = typeBuilder.CreateType()!;
		var payload = new byte[_options.PayloadBytes];
		type.GetField(payloadField.Name)!.SetValue(null, payload);
		var source = Activator.CreateInstance(type)!;

		var label = new MauiLabel();
		label.SetBinding(MauiLabel.TextProperty, new Binding("Value"));
		label.BindingContext = source;

		if (label.Text != "Bound value")
			throw new InvalidOperationException($"Binding did not read the dynamic source. Actual Text: {label.Text ?? "<null>"}");

		assemblyRefs.Add(new WeakReference(type.Assembly, trackResurrection: false));
		typeRefs.Add(new WeakReference(type, trackResurrection: false));
		sourceRefs.Add(new WeakReference(source, trackResurrection: false));
		payloadRefs.Add(new WeakReference(payload, trackResurrection: false));
		liveTargets.Add(label);
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

sealed record ReproOptions(int TypeCount, int PayloadBytes, string? ResultsPath)
{
	public static ReproOptions Parse(string[] args)
	{
		var typeCount = 80;
		var payloadMiB = 1;
		string? resultsPath = null;

		foreach (var arg in args)
		{
			if (arg.StartsWith("--count=", StringComparison.Ordinal))
			{
				typeCount = int.Parse(arg["--count=".Length..]);
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

		if (typeCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(typeCount));
		if (payloadMiB <= 0)
			throw new ArgumentOutOfRangeException(nameof(payloadMiB));

		return new ReproOptions(typeCount, payloadMiB * 1024 * 1024, resultsPath);
	}
}

sealed record ScenarioResult(
	int LiveTargetCount,
	int RetainedAssemblyCount,
	int RetainedTypeCount,
	int RetainedSourceCount,
	int RetainedPayloadCount,
	long RetainedPayloadBytes);

sealed record ReproReport(
	ReproOptions Options,
	ScenarioResult Control,
	ScenarioResult Current)
{
	public override string ToString()
	{
		return $"""
			BindingExpression MethodInfo retention repro

			Dynamic binding source types: {Options.TypeCount}
			Payload per type: {Options.PayloadBytes / 1024 / 1024} MiB

			Control: keep target labels alive but remove bindings after BindingContext clears
			  Live target labels: {Control.LiveTargetCount}
			  Retained assemblies: {Control.RetainedAssemblyCount}/{Options.TypeCount}
			  Retained types: {Control.RetainedTypeCount}/{Options.TypeCount}
			  Retained source instances: {Control.RetainedSourceCount}/{Options.TypeCount}
			  Retained payloads: {Control.RetainedPayloadCount}/{Options.TypeCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}

			Current MAUI: keep target labels and bindings after BindingContext clears
			  Live target labels: {Current.LiveTargetCount}
			  Retained assemblies: {Current.RetainedAssemblyCount}/{Options.TypeCount}
			  Retained types: {Current.RetainedTypeCount}/{Options.TypeCount}
			  Retained source instances: {Current.RetainedSourceCount}/{Options.TypeCount}
			  Retained payloads: {Current.RetainedPayloadCount}/{Options.TypeCount}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			""";
	}
}
