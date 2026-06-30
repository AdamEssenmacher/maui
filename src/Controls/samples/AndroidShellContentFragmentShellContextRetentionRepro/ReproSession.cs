#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using AndroidX.DrawerLayout.Widget;
using AndroidX.Fragment.App;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;

namespace AndroidShellContentFragmentShellContextRetentionRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveFragments,
	int AliveShellContexts,
	int AliveShells,
	int AlivePages,
	int AliveMauiContexts,
	int AliveProviders,
	int AlivePayloadServices,
	int AlivePayloadByteArrays,
	int FragmentsWithShellContext,
	int FragmentsResolvingPayloadService,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveShellContexts == 0 &&
		Control.AliveShells == 0 &&
		Control.AlivePages == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AliveProviders == 0 &&
		Control.AlivePayloadServices == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Control.FragmentsWithShellContext == 0 &&
		Control.FragmentsResolvingPayloadService == 0 &&
		Current.AliveFragments == Attempts &&
		Current.AliveShellContexts == Attempts &&
		Current.AliveShells == Attempts &&
		Current.AlivePages == 0 &&
		Current.AliveMauiContexts == Attempts &&
		Current.AliveProviders == Attempts &&
		Current.AlivePayloadServices == Attempts &&
		Current.AlivePayloadByteArrays == Attempts &&
		Current.FragmentsWithShellContext == Attempts &&
		Current.FragmentsResolvingPayloadService == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidShellContentFragmentShellContextRetentionRepro",
			$"Fragment attempts: {Attempts}",
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
			$"  retained disposed ShellContentFragments: {stats.Attempts}",
			$"  fragments alive after full GC: {stats.AliveFragments}/{stats.Attempts}",
			$"  ShellContexts alive after full GC: {stats.AliveShellContexts}/{stats.Attempts}",
			$"  Shells alive after full GC: {stats.AliveShells}/{stats.Attempts}",
			$"  Pages alive after full GC: {stats.AlivePages}/{stats.Attempts}",
			$"  MauiContexts alive after full GC: {stats.AliveMauiContexts}/{stats.Attempts}",
			$"  service providers alive after full GC: {stats.AliveProviders}/{stats.Attempts}",
			$"  payload services alive after full GC: {stats.AlivePayloadServices}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  retained fragment _shellContext fields: {stats.FragmentsWithShellContext}/{stats.Attempts}",
			$"  fragments resolving payload service: {stats.FragmentsResolvingPayloadService}/{stats.Attempts}",
			$"  retained context payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
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
	const int Attempts = 96;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo ShellContextField =
		typeof(ShellContentFragment).GetField("_shellContext", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(typeof(ShellContentFragment).FullName, "_shellContext");

	public static async Task<ReproReport> RunAsync(IMauiContext hostContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			hostContext,
			"control: clear ShellContentFragment._shellContext after dispose",
			clearShellContext: true);

		var current = await RunScenarioAsync(
			hostContext,
			"current: dispose ShellContentFragment only",
			clearShellContext: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext hostContext, string name, bool clearShellContext)
	{
		var retainedFragments = new List<ShellContentFragment>(Attempts);
		var fragmentRefs = new List<WeakReference<ShellContentFragment>>(Attempts);
		var shellContextRefs = new List<WeakReference<PayloadShellContext>>(Attempts);
		var shellRefs = new List<WeakReference<Shell>>(Attempts);
		var pageRefs = new List<WeakReference<Page>>(Attempts);
		var mauiContextRefs = new List<WeakReference<IMauiContext>>(Attempts);
		var providerRefs = new List<WeakReference<PayloadServiceProvider>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedFragment(
				hostContext,
				clearShellContext,
				retainedFragments,
				fragmentRefs,
				shellContextRefs,
				shellRefs,
				pageRefs,
				mauiContextRefs,
				providerRefs,
				payloadRefs,
				i);

			if (i % 12 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedFragments);

		var aliveFragments = fragmentRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveShellContexts = shellContextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveShells = shellRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePages = pageRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveMauiContexts = mauiContextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveProviders = providerRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadServices = payloadRefs.Count(static wr => wr.PayloadService.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var fragmentsWithShellContext = fragmentRefs.Count(static wr =>
			wr.TryGetTarget(out var fragment) &&
			ShellContextField.GetValue(fragment) is IShellContext);
		var fragmentsResolvingPayloadService = fragmentRefs.Count(static wr =>
			wr.TryGetTarget(out var fragment) &&
			ShellContextField.GetValue(fragment) is PayloadShellContext context &&
			context.MauiContext.Services.GetService(typeof(PayloadService)) is PayloadService);

		return new RunStats(
			name,
			Attempts,
			aliveFragments,
			aliveShellContexts,
			aliveShells,
			alivePages,
			aliveMauiContexts,
			aliveProviders,
			alivePayloadServices,
			alivePayloadByteArrays,
			fragmentsWithShellContext,
			fragmentsResolvingPayloadService,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisposedFragment(
		IMauiContext hostContext,
		bool clearShellContext,
		List<ShellContentFragment> retainedFragments,
		List<WeakReference<ShellContentFragment>> fragmentRefs,
		List<WeakReference<PayloadShellContext>> shellContextRefs,
		List<WeakReference<Shell>> shellRefs,
		List<WeakReference<Page>> pageRefs,
		List<WeakReference<IMauiContext>> mauiContextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var shell = CreateShell(index);
		var context = CreatePayloadShellContext(hostContext, shell, index, out var mauiContext, out var provider, out var payload);
		var page = new ContentPage { Title = $"Fragment page {index:0000}" };
		var fragment = new ShellContentFragment(context, (Page)page);

		fragmentRefs.Add(new WeakReference<ShellContentFragment>(fragment));
		shellContextRefs.Add(new WeakReference<PayloadShellContext>(context));
		shellRefs.Add(new WeakReference<Shell>(shell));
		pageRefs.Add(new WeakReference<Page>(page));
		mauiContextRefs.Add(new WeakReference<IMauiContext>(mauiContext));
		providerRefs.Add(new WeakReference<PayloadServiceProvider>(provider));
		payloadRefs.Add(new PayloadWeakReference(new WeakReference<PayloadService>(payload), new WeakReference<byte[]>(payload.Bytes)));
		retainedFragments.Add(fragment);

		fragment.Dispose();

		if (clearShellContext)
			ShellContextField.SetValue(fragment, null);
	}

	static Shell CreateShell(int index)
	{
		var shell = new Shell
		{
			Title = $"Retired shell {index:0000}"
		};

		shell.Items.Add(new FlyoutItem
		{
			Title = $"Flyout {index:0000}",
			Items =
			{
				new ShellContent
				{
					Title = $"Content {index:0000}",
					Content = new ContentPage { Title = $"Root {index:0000}" }
				}
			}
		});

		return shell;
	}

	static PayloadShellContext CreatePayloadShellContext(
		IMauiContext hostContext,
		Shell shell,
		int index,
		out IMauiContext mauiContext,
		out PayloadServiceProvider provider,
		out PayloadService payload)
	{
		payload = new PayloadService(index, PayloadBytes);
		provider = new PayloadServiceProvider(hostContext.Services, payload);
		var androidContext = hostContext.Context ?? throw new InvalidOperationException("Android context is not available.");
		mauiContext = new MauiContext(provider, androidContext);
		return new PayloadShellContext(androidContext, shell, mauiContext);
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

	sealed class PayloadShellContext : IShellContext
	{
		public PayloadShellContext(Context androidContext, Shell shell, IMauiContext mauiContext)
		{
			AndroidContext = androidContext;
			Shell = shell;
			MauiContext = mauiContext;
		}

		public Context AndroidContext { get; }

		public Shell Shell { get; }

		public IMauiContext MauiContext { get; }

		public DrawerLayout CurrentDrawerLayout => throw new NotSupportedException();

		public IShellObservableFragment CreateFragmentForPage(Page page) => throw new NotSupportedException();

		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() => throw new NotSupportedException();

		public IShellItemRenderer CreateShellItemRenderer(ShellItem shellItem) => throw new NotSupportedException();

		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) => throw new NotSupportedException();

		public IShellToolbarTracker CreateTrackerForToolbar(AToolbar toolbar) => throw new NotSupportedException();

		public IShellToolbarAppearanceTracker CreateToolbarAppearanceTracker() => throw new NotSupportedException();

		public IShellTabLayoutAppearanceTracker CreateTabLayoutAppearanceTracker(ShellSection shellSection) => throw new NotSupportedException();

		public IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) => throw new NotSupportedException();
	}
}
