using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using UIKit;

namespace NativeBindingRetainedPeerLeakRepro;

internal static class ReproSession
{
	const int Cycles = 160;
	const int PayloadBytes = 1024 * 1024;
	const string ResultsPath = "/tmp/nativebinding-retained-peer-leak-repro-results.txt";

	static readonly List<UITextField> s_retainedNativePeers = new();

	public static async Task<bool> RunAsync()
	{
		await Task.Yield();

		ForceGc();
		var baseline = GC.GetTotalMemory(true);

		var control = RunScenario("control", removeNativeBindingProxy: true);
		ForceGc();

		var current = RunScenario("current", removeNativeBindingProxy: false);
		ForceGc();

		var final = GC.GetTotalMemory(true);
		var proved = control.AlivePayloads == 0 &&
			current.AliveNativePeers == Cycles &&
			current.AliveViewModels == Cycles &&
			current.AlivePayloads == Cycles &&
			current.ProxyEntries == Cycles;

		var lines = new List<string>
		{
			"NativeBindingRetainedPeerLeakRepro",
			$"Cycles per scenario: {Cycles.ToString(CultureInfo.InvariantCulture)}",
			$"Payload per binding source: {PayloadBytes / 1024} KiB",
			$"Baseline managed heap: {baseline:N0} bytes",
			$"Final managed heap: {final:N0} bytes",
			$"Managed heap delta: {ToMiB(final - baseline):N1} MiB",
			$"Leak proved: {proved}",
			string.Empty,
			Format(control),
			string.Empty,
			Format(current),
			string.Empty,
			$"RESULT: {(proved ? "PROVEN" : "NOT PROVEN")}"
		};

		await File.WriteAllLinesAsync(ResultsPath, lines);
		return proved;
	}

	static ScenarioResult RunScenario(string name, bool removeNativeBindingProxy)
	{
		var nativePeers = new List<WeakReference<UITextField>>(Cycles);
		var viewModels = new List<WeakReference<PayloadViewModel>>(Cycles);
		var payloads = new List<WeakReference<byte[]>>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			var weakReferences = CreateCycle(i, removeNativeBindingProxy);
			nativePeers.Add(weakReferences.NativePeer);
			viewModels.Add(weakReferences.ViewModel);
			payloads.Add(weakReferences.Payload);
		}

		ForceGc();

		var aliveNativePeers = CountAlive(nativePeers);
		var aliveViewModels = CountAlive(viewModels);
		var alivePayloads = CountAlive(payloads);
		var proxyEntries = s_retainedNativePeers.Count(NativeBindingProxyTable.Contains);
		var retainedPayloadBytes = alivePayloads * PayloadBytes;

		return new ScenarioResult(
			name,
			aliveNativePeers,
			aliveViewModels,
			alivePayloads,
			proxyEntries,
			retainedPayloadBytes);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static CycleWeakReferences CreateCycle(int index, bool removeNativeBindingProxy)
	{
		var nativePeer = new UITextField();
		var viewModel = new PayloadViewModel(index, PayloadBytes);
		var payload = viewModel.Payload;

		nativePeer.SetBinding(
			nameof(UITextField.Text),
			new Binding(nameof(PayloadViewModel.Title), BindingMode.OneWay, source: viewModel));

		if (nativePeer.Text != viewModel.Title)
			throw new InvalidOperationException("Native binding did not initialize the UITextField.Text value.");

		if (removeNativeBindingProxy)
			NativeBindingProxyTable.Remove(nativePeer);

		s_retainedNativePeers.Add(nativePeer);

		return new CycleWeakReferences(
			new WeakReference<UITextField>(nativePeer),
			new WeakReference<PayloadViewModel>(viewModel),
			new WeakReference<byte[]>(payload));
	}

	static string Format(ScenarioResult result) =>
		$"""
		Run: {result.Name}
		  alive retained native UITextFields: {result.AliveNativePeers}/{Cycles}
		  native-binding proxy entries: {result.ProxyEntries}/{Cycles}
		  alive binding source view models: {result.AliveViewModels}/{Cycles}
		  alive binding source payloads: {result.AlivePayloads}/{Cycles}
		  estimated retained payload bytes: {result.RetainedPayloadBytes:N0}
		  estimated retained payload MiB: {ToMiB(result.RetainedPayloadBytes):N1}
		""";

	static int CountAlive<T>(IEnumerable<WeakReference<T>> weakReferences)
		where T : class
	{
		var count = 0;
		foreach (var weakReference in weakReferences)
		{
			if (weakReference.TryGetTarget(out _))
				count++;
		}

		return count;
	}

	static void ForceGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	static double ToMiB(long bytes) => bytes / 1024d / 1024d;

	sealed class PayloadViewModel
	{
		public PayloadViewModel(int index, int payloadBytes)
		{
			Title = $"Bound item {index}";
			Payload = new byte[payloadBytes];
			Payload[0] = (byte)(index % 251);
		}

		public string Title { get; }

		public byte[] Payload { get; }
	}

	readonly record struct ScenarioResult(
		string Name,
		int AliveNativePeers,
		int AliveViewModels,
		int AlivePayloads,
		int ProxyEntries,
		long RetainedPayloadBytes);

	readonly record struct CycleWeakReferences(
		WeakReference<UITextField> NativePeer,
		WeakReference<PayloadViewModel> ViewModel,
		WeakReference<byte[]> Payload);

	static class NativeBindingProxyTable
	{
		static readonly object s_table;
		static readonly MethodInfo s_remove;
		static readonly MethodInfo s_tryGetValue;

		static NativeBindingProxyTable()
		{
			var controlsAssembly = typeof(Binding).Assembly;
			var proxyTypeDefinition = controlsAssembly.GetType(
				"Microsoft.Maui.Controls.Internals.PlatformBindingHelpers+BindableObjectProxy`1",
				throwOnError: true)!;
			var proxyType = proxyTypeDefinition.MakeGenericType(typeof(UIView));
			var tableProperty = proxyType.GetProperty(
				"BindableObjectProxies",
				BindingFlags.Public | BindingFlags.Static)!;

			s_table = tableProperty.GetValue(null)!;
			s_remove = s_table.GetType().GetMethod(nameof(ConditionalWeakTable<object, object>.Remove), new[] { typeof(UIView) })!;
			s_tryGetValue = s_table.GetType().GetMethods()
				.Single(method => method.Name == nameof(ConditionalWeakTable<object, object>.TryGetValue) &&
					method.GetParameters().Length == 2);
		}

		public static bool Remove(UITextField nativePeer) =>
			(bool)s_remove.Invoke(s_table, new object[] { nativePeer })!;

		public static bool Contains(UITextField nativePeer)
		{
			var arguments = new object?[] { nativePeer, null };
			return (bool)s_tryGetValue.Invoke(s_table, arguments)!;
		}
	}
}
