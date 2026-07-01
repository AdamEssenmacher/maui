#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using AndroidX.Fragment.App;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;

namespace AndroidScopedFragmentDetailContextRetentionRepro;

internal static class ReproSession
{
	const int Attempts = 96;
	const int PayloadBytes = 1024 * 1024;

	static readonly Type ScopedFragmentType =
		typeof(IMauiContext).Assembly.GetType("Microsoft.Maui.Platform.ScopedFragment", throwOnError: true)
		?? throw new MissingMemberException("Microsoft.Maui.Platform.ScopedFragment");

	static readonly ConstructorInfo ScopedFragmentConstructor =
		ScopedFragmentType.GetConstructor(
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			types: new[] { typeof(IView), typeof(IMauiContext) },
			modifiers: null)
		?? throw new MissingMethodException(ScopedFragmentType.FullName, ".ctor(IView, IMauiContext)");

	static readonly FieldInfo FragmentMauiContextField =
		ScopedFragmentType.GetField("_mauiContext", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(ScopedFragmentType.FullName, "_mauiContext");

	static readonly FieldInfo FragmentDetailViewField =
		ScopedFragmentType.GetField("<DetailView>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(ScopedFragmentType.FullName, "<DetailView>k__BackingField");

	static readonly List<object> RetainedNativePeerRoots = new();

	public static async Task<ReproReport> RunAsync(IMauiContext hostContext)
	{
		await Task.Yield();

		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			hostContext,
			"control: clear ScopedFragment DetailView and _mauiContext after OnDestroy",
			clearFragmentFields: true);

		var current = await RunScenarioAsync(
			hostContext,
			"current: ScopedFragment OnDestroy leaves DetailView and _mauiContext assigned",
			clearFragmentFields: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);
		GC.KeepAlive(RetainedNativePeerRoots);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(
		IMauiContext hostContext,
		string name,
		bool clearFragmentFields)
	{
		var retainedFragments = new List<NativePeerRoot>(Attempts);
		var fragmentRefs = new List<WeakReference<Fragment>>(Attempts);
		var detailViewRefs = new List<WeakReference<BoxView>>(Attempts);
		var handlerRefs = new List<WeakReference<IElementHandler>>(Attempts);
		var contextRefs = new List<WeakReference<IMauiContext>>(Attempts);
		var providerRefs = new List<WeakReference<PayloadServiceProvider>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDestroyedFragment(
				hostContext,
				clearFragmentFields,
				retainedFragments,
				fragmentRefs,
				detailViewRefs,
				handlerRefs,
				contextRefs,
				providerRefs,
				payloadRefs,
				i);

			if (i % 12 == 0)
				await Task.Yield();
		}

		RetainedNativePeerRoots.Add(retainedFragments);
		ForceFullGc();
		GC.KeepAlive(retainedFragments);

		var aliveFragments = fragmentRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveDetailViews = detailViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveHandlers = handlerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveContexts = contextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveProviders = providerRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadServices = payloadRefs.Count(static wr => wr.PayloadService.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var fragmentsWithDetailView = fragmentRefs.Count(static wr =>
			wr.TryGetTarget(out var fragment) &&
			FragmentDetailViewField.GetValue(fragment) is IView);
		var fragmentsWithMauiContext = fragmentRefs.Count(static wr =>
			wr.TryGetTarget(out var fragment) &&
			FragmentMauiContextField.GetValue(fragment) is IMauiContext);
		var fragmentsResolvingPayloadService = fragmentRefs.Count(static wr =>
			wr.TryGetTarget(out var fragment) &&
			FragmentMauiContextField.GetValue(fragment) is IMauiContext context &&
			context.Services.GetService(typeof(PayloadService)) is PayloadService);
		var retainedNativeGlobalRefs = retainedFragments.Count(static root => root.GlobalRef != IntPtr.Zero);

		return new RunStats(
			name,
			Attempts,
			retainedNativeGlobalRefs,
			aliveFragments,
			aliveDetailViews,
			aliveHandlers,
			aliveContexts,
			aliveProviders,
			alivePayloadServices,
			alivePayloadByteArrays,
			fragmentsWithDetailView,
			fragmentsWithMauiContext,
			fragmentsResolvingPayloadService,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDestroyedFragment(
		IMauiContext hostContext,
		bool clearFragmentFields,
		List<NativePeerRoot> retainedFragments,
		List<WeakReference<Fragment>> fragmentRefs,
		List<WeakReference<BoxView>> detailViewRefs,
		List<WeakReference<IElementHandler>> handlerRefs,
		List<WeakReference<IMauiContext>> contextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new PayloadService(index, PayloadBytes);
		var provider = new PayloadServiceProvider(hostContext.Services, payload);
		var androidContext = hostContext.Context ?? throw new InvalidOperationException("Android context is not available.");
		var mauiContext = new MauiContext(provider, androidContext);
		var detailView = new BoxView
		{
			WidthRequest = 48,
			HeightRequest = 48,
			Color = Colors.SlateGray,
			BindingContext = payload
		};

		_ = detailView.ToPlatform(mauiContext);
		var handler = detailView.Handler ?? throw new InvalidOperationException("The detail view did not create a handler.");
		var fragment = CreateScopedFragment(detailView, mauiContext);
		var nativePeer = NativePeerRoot.Create(fragment);

		payloadRefs.Add(new PayloadWeakReference(new WeakReference<PayloadService>(payload), new WeakReference<byte[]>(payload.Bytes)));
		providerRefs.Add(new WeakReference<PayloadServiceProvider>(provider));
		contextRefs.Add(new WeakReference<IMauiContext>(mauiContext));
		detailViewRefs.Add(new WeakReference<BoxView>(detailView));
		handlerRefs.Add(new WeakReference<IElementHandler>(handler));
		fragmentRefs.Add(new WeakReference<Fragment>(fragment));
		retainedFragments.Add(nativePeer);

		detailView.BindingContext = null;
		handler.DisconnectHandler();
		detailView.Handler = null;
		fragment.OnDestroy();

		if (clearFragmentFields)
			ClearScopedFragmentFields(fragment);
	}

	static Fragment CreateScopedFragment(IView detailView, IMauiContext mauiContext) =>
		(Fragment)ScopedFragmentConstructor.Invoke(new object[] { detailView, mauiContext });

	static void ClearScopedFragmentFields(Fragment fragment)
	{
		FragmentDetailViewField.SetValue(fragment, null);
		FragmentMauiContextField.SetValue(fragment, null);
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(75);
		}
	}

	sealed record NativePeerRoot(IntPtr GlobalRef)
	{
		public static NativePeerRoot Create(Fragment fragment)
		{
			if (fragment.Handle == IntPtr.Zero)
				throw new InvalidOperationException("Native Fragment handle was not available before cleanup.");

			var globalRef = JNIEnv.NewGlobalRef(fragment.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException("Failed to create a JNI global reference for the native Fragment.");

			return new NativePeerRoot(globalRef);
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

internal sealed record RunStats(
	string Name,
	int Attempts,
	int RetainedNativeGlobalRefs,
	int AliveFragments,
	int AliveDetailViews,
	int AliveHandlers,
	int AliveContexts,
	int AliveProviders,
	int AlivePayloadServices,
	int AlivePayloadByteArrays,
	int FragmentsWithDetailView,
	int FragmentsWithMauiContext,
	int FragmentsResolvingPayloadService,
	long RetainedPayloadBytes);

internal sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.RetainedNativeGlobalRefs == Attempts &&
		Current.RetainedNativeGlobalRefs == Attempts &&
		Control.AliveFragments == Attempts &&
		Current.AliveFragments == Attempts &&
		Control.AliveDetailViews == 0 &&
		Control.AliveHandlers == 0 &&
		Control.AliveContexts == 0 &&
		Control.AliveProviders == 0 &&
		Control.AlivePayloadServices == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Control.FragmentsWithDetailView == 0 &&
		Control.FragmentsWithMauiContext == 0 &&
		Control.FragmentsResolvingPayloadService == 0 &&
		Current.AliveDetailViews == Attempts &&
		Current.AliveHandlers == 0 &&
		Current.AliveContexts == Attempts &&
		Current.AliveProviders == Attempts &&
		Current.AlivePayloadServices == Attempts &&
		Current.AlivePayloadByteArrays == Attempts &&
		Current.FragmentsWithDetailView == Attempts &&
		Current.FragmentsWithMauiContext == Attempts &&
		Current.FragmentsResolvingPayloadService == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidScopedFragmentDetailContextRetentionRepro",
			$"Retained destroyed ScopedFragment peers: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
			"Source path exercised: Microsoft.Maui.Platform.ScopedFragment.OnDestroy()",
			"Both runs disconnect the hosted BoxView handler and clear the view BindingContext before fragment destruction.",
			"Control-only cleanup clears private ScopedFragment DetailView and _mauiContext fields after OnDestroy().",
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
			$"  retained JNI global refs to native Fragments: {stats.RetainedNativeGlobalRefs}/{stats.Attempts}",
			$"  ScopedFragments alive after full GC: {stats.AliveFragments}/{stats.Attempts}",
			$"  detail BoxViews alive after full GC: {stats.AliveDetailViews}/{stats.Attempts}",
			$"  hosted handlers alive after full GC: {stats.AliveHandlers}/{stats.Attempts}",
			$"  MauiContexts alive after full GC: {stats.AliveContexts}/{stats.Attempts}",
			$"  service providers alive after full GC: {stats.AliveProviders}/{stats.Attempts}",
			$"  payload services alive after full GC: {stats.AlivePayloadServices}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  retained ScopedFragment.DetailView fields: {stats.FragmentsWithDetailView}/{stats.Attempts}",
			$"  retained ScopedFragment._mauiContext fields: {stats.FragmentsWithMauiContext}/{stats.Attempts}",
			$"  retained fragments resolving payload service: {stats.FragmentsResolvingPayloadService}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
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
