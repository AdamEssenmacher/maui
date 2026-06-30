#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AndroidX.AppCompat.App;
using AndroidX.Fragment.App;
using AndroidX.ViewPager2.Adapter;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;

namespace AndroidShellFragmentMauiContextRetentionRepro;

public sealed record RunStats(
	string Name,
	int AdapterAttempts,
	int FragmentAttempts,
	int AliveAdapters,
	int AliveFragments,
	int AliveContexts,
	int AliveProviders,
	int AlivePayloadServices,
	int AlivePayloadByteArrays,
	int AdaptersWithMauiContext,
	int FragmentsWithMauiContext,
	int AdaptersResolvingPayloadService,
	int FragmentsResolvingPayloadService,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int AdapterAttempts,
	int FragmentAttempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public int TotalAttempts => AdapterAttempts + FragmentAttempts;

	public bool LeakProved =>
		Control.AliveContexts == 0 &&
		Control.AliveProviders == 0 &&
		Control.AlivePayloadServices == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Control.AdaptersWithMauiContext == 0 &&
		Control.FragmentsWithMauiContext == 0 &&
		Control.AdaptersResolvingPayloadService == 0 &&
		Control.FragmentsResolvingPayloadService == 0 &&
		Current.AliveAdapters == AdapterAttempts &&
		Current.AliveFragments == FragmentAttempts &&
		Current.AliveContexts == TotalAttempts &&
		Current.AliveProviders == TotalAttempts &&
		Current.AlivePayloadServices == TotalAttempts &&
		Current.AlivePayloadByteArrays == TotalAttempts &&
		Current.AdaptersWithMauiContext == AdapterAttempts &&
		Current.FragmentsWithMauiContext == FragmentAttempts &&
		Current.AdaptersResolvingPayloadService == AdapterAttempts &&
		Current.FragmentsResolvingPayloadService == FragmentAttempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidShellFragmentMauiContextRetentionRepro",
			$"Adapter attempts: {AdapterAttempts}",
			$"Fragment attempts: {FragmentAttempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}",
			LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained disposed ShellFragmentStateAdapters: {stats.AdapterAttempts}",
			$"  retained disposed ShellFragmentContainers: {stats.FragmentAttempts}",
			$"  adapters alive after full GC: {stats.AliveAdapters}/{stats.AdapterAttempts}",
			$"  fragments alive after full GC: {stats.AliveFragments}/{stats.FragmentAttempts}",
			$"  MauiContexts alive after full GC: {stats.AliveContexts}/{stats.AdapterAttempts + stats.FragmentAttempts}",
			$"  service providers alive after full GC: {stats.AliveProviders}/{stats.AdapterAttempts + stats.FragmentAttempts}",
			$"  payload services alive after full GC: {stats.AlivePayloadServices}/{stats.AdapterAttempts + stats.FragmentAttempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.AdapterAttempts + stats.FragmentAttempts}",
			$"  retained adapter _mauiContext fields: {stats.AdaptersWithMauiContext}/{stats.AdapterAttempts}",
			$"  retained fragment _mauiContext fields: {stats.FragmentsWithMauiContext}/{stats.FragmentAttempts}",
			$"  adapters resolving payload service: {stats.AdaptersResolvingPayloadService}/{stats.AdapterAttempts}",
			$"  fragments resolving payload service: {stats.FragmentsResolvingPayloadService}/{stats.FragmentAttempts}",
			$"  retained context payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * (stats.AdapterAttempts + stats.FragmentAttempts)):0.0}%)");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : "";
		var value = Math.Abs((double)bytes);
		if (value >= 1024 * 1024)
			return $"{sign}{value / 1024 / 1024:0.0} MiB";
		if (value >= 1024)
			return $"{sign}{value / 1024:0.0} KiB";
		return $"{bytes} B";
	}
}

internal static class ReproSession
{
	const int AdapterAttempts = 48;
	const int FragmentAttempts = 48;
	const int PayloadBytes = 1024 * 1024;

	static readonly Type ShellFragmentStateAdapterType =
		typeof(Shell).Assembly.GetType("Microsoft.Maui.Controls.Platform.Compatibility.ShellFragmentStateAdapter")
		?? throw new MissingMemberException("ShellFragmentStateAdapter");

	static readonly Type ShellFragmentContainerType =
		typeof(Shell).Assembly.GetType("Microsoft.Maui.Controls.Platform.Compatibility.ShellFragmentContainer")
		?? throw new MissingMemberException("ShellFragmentContainer");

	static readonly FieldInfo AdapterMauiContextField =
		ShellFragmentStateAdapterType.GetField("_mauiContext", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(ShellFragmentStateAdapterType.FullName, "_mauiContext");

	static readonly FieldInfo FragmentMauiContextField =
		ShellFragmentContainerType.GetField("_mauiContext", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(ShellFragmentContainerType.FullName, "_mauiContext");

	public static async Task<ReproReport> RunAsync(IMauiContext hostContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			hostContext,
			"control: clear Shell fragment adapter/container _mauiContext fields",
			clearMauiContext: true);

		var current = await RunScenarioAsync(
			hostContext,
			"current: dispose Shell fragment adapter/container only",
			clearMauiContext: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(AdapterAttempts, FragmentAttempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext hostContext, string name, bool clearMauiContext)
	{
		var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as AppCompatActivity
			?? throw new InvalidOperationException("The current activity is not an AppCompatActivity.");
		var fragmentManager = activity.SupportFragmentManager;

		var retainedAdapters = new List<FragmentStateAdapter>(AdapterAttempts);
		var retainedFragments = new List<Fragment>(FragmentAttempts);
		var adapterRefs = new List<WeakReference<FragmentStateAdapter>>(AdapterAttempts);
		var fragmentRefs = new List<WeakReference<Fragment>>(FragmentAttempts);
		var contextRefs = new List<WeakReference<IMauiContext>>(AdapterAttempts + FragmentAttempts);
		var providerRefs = new List<WeakReference<PayloadServiceProvider>>(AdapterAttempts + FragmentAttempts);
		var payloadRefs = new List<PayloadWeakReference>(AdapterAttempts + FragmentAttempts);

		for (var i = 0; i < AdapterAttempts; i++)
		{
			CreateDisposedAdapter(
				hostContext,
				fragmentManager,
				clearMauiContext,
				retainedAdapters,
				adapterRefs,
				contextRefs,
				providerRefs,
				payloadRefs,
				i);

			if (i % 12 == 0)
				await Task.Yield();
		}

		for (var i = 0; i < FragmentAttempts; i++)
		{
			CreateDisposedFragment(
				hostContext,
				fragmentManager,
				clearMauiContext,
				retainedFragments,
				fragmentRefs,
				contextRefs,
				providerRefs,
				payloadRefs,
				i + AdapterAttempts);

			if (i % 12 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedAdapters);
		GC.KeepAlive(retainedFragments);

		var aliveAdapters = adapterRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveFragments = fragmentRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveContexts = contextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveProviders = providerRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadServices = payloadRefs.Count(static wr => wr.PayloadService.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var adaptersWithMauiContext = adapterRefs.Count(static wr =>
			wr.TryGetTarget(out var adapter) &&
			AdapterMauiContextField.GetValue(adapter) is IMauiContext);
		var fragmentsWithMauiContext = fragmentRefs.Count(static wr =>
			wr.TryGetTarget(out var fragment) &&
			FragmentMauiContextField.GetValue(fragment) is IMauiContext);
		var adaptersResolvingPayloadService = adapterRefs.Count(static wr =>
			wr.TryGetTarget(out var adapter) &&
			AdapterMauiContextField.GetValue(adapter) is IMauiContext context &&
			context.Services.GetService(typeof(PayloadService)) is PayloadService);
		var fragmentsResolvingPayloadService = fragmentRefs.Count(static wr =>
			wr.TryGetTarget(out var fragment) &&
			FragmentMauiContextField.GetValue(fragment) is IMauiContext context &&
			context.Services.GetService(typeof(PayloadService)) is PayloadService);

		return new RunStats(
			name,
			AdapterAttempts,
			FragmentAttempts,
			aliveAdapters,
			aliveFragments,
			aliveContexts,
			aliveProviders,
			alivePayloadServices,
			alivePayloadByteArrays,
			adaptersWithMauiContext,
			fragmentsWithMauiContext,
			adaptersResolvingPayloadService,
			fragmentsResolvingPayloadService,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisposedAdapter(
		IMauiContext hostContext,
		FragmentManager fragmentManager,
		bool clearMauiContext,
		List<FragmentStateAdapter> retainedAdapters,
		List<WeakReference<FragmentStateAdapter>> adapterRefs,
		List<WeakReference<IMauiContext>> contextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var context = CreatePayloadContext(hostContext, index, out var provider, out var payload);
		var adapter = CreateShellFragmentStateAdapter(CreateShellSection(index), fragmentManager, context);

		TrackPayload(context, provider, payload, contextRefs, providerRefs, payloadRefs);
		adapterRefs.Add(new WeakReference<FragmentStateAdapter>(adapter));
		retainedAdapters.Add(adapter);

		adapter.Dispose();

		if (clearMauiContext)
			AdapterMauiContextField.SetValue(adapter, null);
	}

	static void CreateDisposedFragment(
		IMauiContext hostContext,
		FragmentManager fragmentManager,
		bool clearMauiContext,
		List<Fragment> retainedFragments,
		List<WeakReference<Fragment>> fragmentRefs,
		List<WeakReference<IMauiContext>> contextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var context = CreatePayloadContext(hostContext, index, out var provider, out var payload);
		var adapter = CreateShellFragmentStateAdapter(CreateShellSection(index), fragmentManager, context);
		var fragment = adapter.CreateFragment(0);

		TrackPayload(context, provider, payload, contextRefs, providerRefs, payloadRefs);
		fragmentRefs.Add(new WeakReference<Fragment>(fragment));
		retainedFragments.Add(fragment);

		// Isolate ShellFragmentContainer._mauiContext from the adapter field in both runs.
		AdapterMauiContextField.SetValue(adapter, null);
		adapter.Dispose();
		fragment.Dispose();

		if (clearMauiContext)
			FragmentMauiContextField.SetValue(fragment, null);
	}

	static MauiContext CreatePayloadContext(
		IMauiContext hostContext,
		int index,
		out PayloadServiceProvider provider,
		out PayloadService payload)
	{
		payload = new PayloadService(index, PayloadBytes);
		provider = new PayloadServiceProvider(hostContext.Services, payload);
		var androidContext = hostContext.Context ?? throw new InvalidOperationException("Android context is not available.");
		return new MauiContext(provider, androidContext);
	}

	static FragmentStateAdapter CreateShellFragmentStateAdapter(
		ShellSection shellSection,
		FragmentManager fragmentManager,
		IMauiContext mauiContext)
	{
		return (FragmentStateAdapter)(Activator.CreateInstance(
			ShellFragmentStateAdapterType,
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			args: new object[] { shellSection, fragmentManager, mauiContext },
			culture: null)
			?? throw new InvalidOperationException("Could not create ShellFragmentStateAdapter."));
	}

	static ShellSection CreateShellSection(int index)
	{
		var section = new ShellSection { Title = $"Section {index:0000}" };
		section.Items.Add(new ShellContent { Title = $"Content {index:0000}" });
		return section;
	}

	static void TrackPayload(
		IMauiContext context,
		PayloadServiceProvider provider,
		PayloadService payload,
		List<WeakReference<IMauiContext>> contextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReference> payloadRefs)
	{
		contextRefs.Add(new WeakReference<IMauiContext>(context));
		providerRefs.Add(new WeakReference<PayloadServiceProvider>(provider));
		payloadRefs.Add(new PayloadWeakReference(new WeakReference<PayloadService>(payload), new WeakReference<byte[]>(payload.Bytes)));
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(50);
		}
	}

	sealed record PayloadWeakReference(WeakReference<PayloadService> PayloadService, WeakReference<byte[]> Bytes);

	sealed class PayloadService
	{
		public PayloadService(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)((id + i) % 251);
			Bytes[^1] = (byte)((id + Bytes.Length) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}

	sealed class PayloadServiceProvider : IServiceProvider
	{
		readonly IServiceProvider _fallback;
		readonly PayloadService _payload;

		public PayloadServiceProvider(IServiceProvider fallback, PayloadService payload)
		{
			_fallback = fallback;
			_payload = payload;
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(PayloadService))
				return _payload;

			return _fallback.GetService(serviceType);
		}
	}
}
