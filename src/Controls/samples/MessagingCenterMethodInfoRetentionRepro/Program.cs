using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;

var options = ReproOptions.Parse(args);
var probe = new MessagingCenterMethodInfoRetentionProbe(options);
var report = probe.Run();

Console.WriteLine(report);

if (!string.IsNullOrWhiteSpace(options.ResultsPath))
{
	var resultsDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ResultsPath));
	if (!string.IsNullOrEmpty(resultsDirectory))
		Directory.CreateDirectory(resultsDirectory);

	File.WriteAllText(options.ResultsPath, report.ToString());
}

return report.Current.SubscriptionCount == options.TypeCount
	&& report.Current.RetainedAssemblyCount == options.TypeCount
	&& report.Current.RetainedTypeCount == options.TypeCount
	&& report.Current.RetainedPayloadCount == options.TypeCount
	&& report.Current.RetainedSubscriberCount == 0
	&& report.Control.SubscriptionCount == 0
	&& report.Control.RetainedAssemblyCount == 0
	&& report.Control.RetainedTypeCount == 0
	&& report.Control.RetainedPayloadCount == 0
	&& report.Control.RetainedSubscriberCount == 0
	? 0
	: 1;

sealed class MessagingCenterMethodInfoRetentionProbe
{
	const string MessageName = "MessagingCenterMethodInfoRetentionRepro";
	readonly ReproOptions _options;
	readonly MessagingCenterReflection _messagingCenter = new();

	public MessagingCenterMethodInfoRetentionProbe(ReproOptions options)
	{
		_options = options;
	}

	public ReproReport Run()
	{
		var control = RunScenario(clearSubscriptionsBeforeFinalCollect: true);
		var current = RunScenario(clearSubscriptionsBeforeFinalCollect: false);

		return new ReproReport(_options, control, current);
	}

	ScenarioResult RunScenario(bool clearSubscriptionsBeforeFinalCollect)
	{
		_messagingCenter.ClearSubscribers();

		var assemblyRefs = new List<WeakReference>(_options.TypeCount);
		var typeRefs = new List<WeakReference>(_options.TypeCount);
		var payloadRefs = new List<WeakReference>(_options.TypeCount);
		var subscriberRefs = new List<WeakReference>(_options.TypeCount);

		for (var i = 0; i < _options.TypeCount; i++)
			SubscribeDynamicSubscriber(i, assemblyRefs, typeRefs, payloadRefs, subscriberRefs);

		CollectHard();

		if (clearSubscriptionsBeforeFinalCollect)
		{
			_messagingCenter.ClearSubscribers();
		}
		else
		{
			// Sending does not prune dead subscribers; it only skips them.
			for (var i = 0; i < 3; i++)
				_messagingCenter.Send(new TestPublisher(), MessageName);
		}

		CollectHard();

		var retainedPayloads = payloadRefs.Count(static wr => wr.IsAlive);

		return new ScenarioResult(
			SubscriptionCount: _messagingCenter.GetSubscriptionCount(),
			RetainedAssemblyCount: assemblyRefs.Count(static wr => wr.IsAlive),
			RetainedTypeCount: typeRefs.Count(static wr => wr.IsAlive),
			RetainedSubscriberCount: subscriberRefs.Count(static wr => wr.IsAlive),
			RetainedPayloadCount: retainedPayloads,
			RetainedPayloadBytes: (long)retainedPayloads * _options.PayloadBytes);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	void SubscribeDynamicSubscriber(
		int index,
		List<WeakReference> assemblyRefs,
		List<WeakReference> typeRefs,
		List<WeakReference> payloadRefs,
		List<WeakReference> subscriberRefs)
	{
		var assemblyName = new AssemblyName($"MessagingCenterMethodInfoRetentionRepro{index}");
		var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndCollect);
		var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");
		var typeBuilder = moduleBuilder.DefineType(
			$"DynamicMessagingCenterSubscriber{index}",
			TypeAttributes.Public | TypeAttributes.Class);

		var payloadField = typeBuilder.DefineField(
			"Payload",
			typeof(byte[]),
			FieldAttributes.Public | FieldAttributes.Static);

		typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);

		var callbackMethod = typeBuilder.DefineMethod(
			"OnMessage",
			MethodAttributes.Public,
			typeof(void),
			new[] { typeof(TestPublisher) });

		callbackMethod.GetILGenerator().Emit(OpCodes.Ret);

		var type = typeBuilder.CreateType()!;
		var payload = new byte[_options.PayloadBytes];
		type.GetField(payloadField.Name)!.SetValue(null, payload);
		var subscriber = Activator.CreateInstance(type)!;
		var callback = (Action<TestPublisher>)Delegate.CreateDelegate(
			typeof(Action<TestPublisher>),
			subscriber,
			type.GetMethod(callbackMethod.Name)!);

		assemblyRefs.Add(new WeakReference(type.Assembly, trackResurrection: false));
		typeRefs.Add(new WeakReference(type, trackResurrection: false));
		payloadRefs.Add(new WeakReference(payload, trackResurrection: false));
		subscriberRefs.Add(new WeakReference(subscriber, trackResurrection: false));

		_messagingCenter.Subscribe(subscriber, MessageName, callback);
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

sealed class MessagingCenterReflection
{
	readonly Type _messagingCenterType;
	readonly MethodInfo _subscribeMethod;
	readonly MethodInfo _sendMethod;
	readonly MethodInfo _clearSubscribersMethod;
	readonly FieldInfo _subscriptionsField;
	readonly object _instance;

	public MessagingCenterReflection()
	{
		_messagingCenterType = typeof(BindableObject).Assembly.GetType(
			"Microsoft.Maui.Controls.MessagingCenter",
			throwOnError: true)!;

		_subscribeMethod = _messagingCenterType
			.GetMethods(BindingFlags.Static | BindingFlags.Public)
			.Single(static method =>
				method.Name == "Subscribe" &&
				method.IsGenericMethodDefinition &&
				method.GetGenericArguments().Length == 1);

		_sendMethod = _messagingCenterType
			.GetMethods(BindingFlags.Static | BindingFlags.Public)
			.Single(static method =>
				method.Name == "Send" &&
				method.IsGenericMethodDefinition &&
				method.GetGenericArguments().Length == 1);

		_clearSubscribersMethod = _messagingCenterType.GetMethod(
			"ClearSubscribers",
			BindingFlags.Static | BindingFlags.NonPublic)
			?? throw new MissingMethodException(_messagingCenterType.FullName, "ClearSubscribers");

		_instance = _messagingCenterType.GetProperty(
			"Instance",
			BindingFlags.Static | BindingFlags.Public)!
			.GetValue(null)!;

		_subscriptionsField = _messagingCenterType.GetField(
			"_subscriptions",
			BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(_messagingCenterType.FullName, "_subscriptions");
	}

	public void Subscribe(object subscriber, string message, Action<TestPublisher> callback)
	{
		_subscribeMethod.MakeGenericMethod(typeof(TestPublisher))
			.Invoke(null, new object?[] { subscriber, message, callback, null });
	}

	public void Send(TestPublisher sender, string message)
	{
		_sendMethod.MakeGenericMethod(typeof(TestPublisher))
			.Invoke(null, new object[] { sender, message });
	}

	public void ClearSubscribers()
	{
		_clearSubscribersMethod.Invoke(null, null);
	}

	public int GetSubscriptionCount()
	{
		if (_subscriptionsField.GetValue(_instance) is not IDictionary subscriptions)
			return -1;

		var count = 0;
		foreach (IList list in subscriptions.Values)
			count += list.Count;

		return count;
	}
}

sealed class TestPublisher
{
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
			MessagingCenter MethodInfo retention repro

			Dynamic subscriber types: {Options.TypeCount}
			Payload per type: {Options.PayloadBytes / 1024 / 1024} MiB

			Control: clear MessagingCenter singleton subscriptions after subscribers collect
			  MessagingCenter subscriptions: {Control.SubscriptionCount}
			  Retained assemblies: {Control.RetainedAssemblyCount}/{Options.TypeCount}
			  Retained types: {Control.RetainedTypeCount}/{Options.TypeCount}
			  Retained subscribers: {Control.RetainedSubscriberCount}/{Options.TypeCount}
			  Retained payloads: {Control.RetainedPayloadCount}/{Options.TypeCount}
			  Retained payload bytes: {Control.RetainedPayloadBytes:N0}

			Current MAUI: singleton remains after subscribers collect and messages are sent
			  MessagingCenter subscriptions: {Current.SubscriptionCount}
			  Retained assemblies: {Current.RetainedAssemblyCount}/{Options.TypeCount}
			  Retained types: {Current.RetainedTypeCount}/{Options.TypeCount}
			  Retained subscribers: {Current.RetainedSubscriberCount}/{Options.TypeCount}
			  Retained payloads: {Current.RetainedPayloadCount}/{Options.TypeCount}
			  Retained payload bytes: {Current.RetainedPayloadBytes:N0}
			""";
	}
}
