using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Maui;

var options = ReproOptions.Parse(args);
var probe = new WeakEventManagerMethodInfoRetentionProbe(options);
var report = probe.Run();

Console.WriteLine(report);

if (!string.IsNullOrWhiteSpace(options.ResultsPath))
{
	var resultsDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ResultsPath));
	if (!string.IsNullOrEmpty(resultsDirectory))
		Directory.CreateDirectory(resultsDirectory);

	File.WriteAllText(options.ResultsPath, report.ToString());
}

return report.Current.RetainedTypeCount == options.TypeCount
	&& report.Current.RetainedPayloadCount == options.TypeCount
	&& report.Current.SubscriptionCount == options.TypeCount
	&& report.Control.RetainedTypeCount == 0
	&& report.Control.RetainedPayloadCount == 0
	&& report.Control.SubscriptionCount == 0
	? 0
	: 1;

sealed class WeakEventManagerMethodInfoRetentionProbe
{
	const string EventName = "PayloadChanged";
	readonly ReproOptions _options;

	public WeakEventManagerMethodInfoRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(pruneDeadSubscriptionsBeforeFinalCollect: true);
		var current = RunScenario(pruneDeadSubscriptionsBeforeFinalCollect: false);

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool pruneDeadSubscriptionsBeforeFinalCollect)
	{
		var manager = new WeakEventManager();
		var assemblyRefs = new List<WeakReference>(_options.TypeCount);
		var typeRefs = new List<WeakReference>(_options.TypeCount);
		var payloadRefs = new List<WeakReference>(_options.TypeCount);
		var subscriberRefs = new List<WeakReference>(_options.TypeCount);

		for (var i = 0; i < _options.TypeCount; i++)
			SubscribeDynamicSubscriber(i, manager, assemblyRefs, typeRefs, payloadRefs, subscriberRefs);

		CollectHard();

		if (pruneDeadSubscriptionsBeforeFinalCollect)
			manager.HandleEvent(sender: null, args: EventArgs.Empty, EventName);

		CollectHard();

		var retainedPayloads = payloadRefs.Count(static wr => wr.IsAlive);

		return new ScenarioResult(
			SubscriptionCount: GetSubscriptionCount(manager),
			RetainedAssemblyCount: assemblyRefs.Count(static wr => wr.IsAlive),
			RetainedTypeCount: typeRefs.Count(static wr => wr.IsAlive),
			RetainedSubscriberCount: subscriberRefs.Count(static wr => wr.IsAlive),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	void SubscribeDynamicSubscriber(
		int index,
		WeakEventManager manager,
		List<WeakReference> assemblyRefs,
		List<WeakReference> typeRefs,
		List<WeakReference> payloadRefs,
		List<WeakReference> subscriberRefs)
	{
		var assemblyName = new AssemblyName($"WeakEventManagerMethodInfoRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");
		var typeBuilder = moduleBuilder.DefineType(
			$"DynamicWeakEventSubscriber{index}",
			TypeAttributes.Public | TypeAttributes.Class);

		var payloadField = typeBuilder.DefineField(
			"Payload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

		typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);

		var handlerMethod = typeBuilder.DefineMethod(
			"OnPayloadChanged",
			MethodAttributes.Public,
			typeof(void),
			new[] { typeof(object), typeof(EventArgs) });

		handlerMethod.GetILGenerator().Emit(OpCodes.Ret);

		var type = typeBuilder.CreateType()!;
		var payload = new byte[_options.PayloadBytes];
		type.GetField(payloadField.Name)!.SetValue(null, payload);
		var subscriber = Activator.CreateInstance(type)!;
		var handler = (EventHandler)Delegate.CreateDelegate(
			typeof(EventHandler),
			subscriber,
			type.GetMethod(handlerMethod.Name)!);

		assemblyRefs.Add(new WeakReference(type.Assembly, trackResurrection: false));
		typeRefs.Add(new WeakReference(type, trackResurrection: false));
		payloadRefs.Add(new WeakReference(payload, trackResurrection: false));
		subscriberRefs.Add(new WeakReference(subscriber, trackResurrection: false));

		manager.AddEventHandler(handler, EventName);
	}

	static int GetSubscriptionCount(WeakEventManager manager)
	{
		var field = typeof(WeakEventManager).GetField("_eventHandlers", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(WeakEventManager).FullName, "_eventHandlers");

		if (field.GetValue(manager) is not IDictionary eventHandlers)
			return -1;

		var count = 0;
		foreach (IList subscriptions in eventHandlers.Values)
			count += subscriptions.Count;

		return count;
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
	int SubscriptionCount,
	int RetainedAssemblyCount,
	int RetainedTypeCount,
	int RetainedSubscriberCount,
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
			WeakEventManager MethodInfo retention repro

			Dynamic subscriber types: {Options.TypeCount}
			Payload per type: {Options.PayloadBytes / 1024 / 1024} MiB

			Control: fire weak event once after subscribers collect
			  WeakEventManager subscriptions: {Control.SubscriptionCount}
			  Retained assemblies: {Control.RetainedAssemblyCount}/{Options.TypeCount}
			  Retained types: {Control.RetainedTypeCount}/{Options.TypeCount}
			  Retained subscribers: {Control.RetainedSubscriberCount}/{Options.TypeCount}
			  Retained payloads: {Control.RetainedPayloadCount}/{Options.TypeCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}

			Current MAUI: publisher remains idle after subscribers collect
			  WeakEventManager subscriptions: {Current.SubscriptionCount}
			  Retained assemblies: {Current.RetainedAssemblyCount}/{Options.TypeCount}
			  Retained types: {Current.RetainedTypeCount}/{Options.TypeCount}
			  Retained subscribers: {Current.RetainedSubscriberCount}/{Options.TypeCount}
			  Retained payloads: {Current.RetainedPayloadCount}/{Options.TypeCount}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			""";
	}
}
