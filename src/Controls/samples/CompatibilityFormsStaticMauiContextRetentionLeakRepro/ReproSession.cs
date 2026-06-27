#nullable enable
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility;

namespace CompatibilityFormsStaticMauiContextRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int AliveContexts,
	int AliveScopes,
	int AliveServices,
	int AlivePayloads,
	long RetainedPayloadBytes,
	string? StaticMauiContextType,
	bool StaticMauiContextCanResolvePayload);

public sealed record ReproReport(
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveContexts == 0 &&
		Control.AliveScopes == 0 &&
		Control.AliveServices == 0 &&
		Control.AlivePayloads == 0 &&
		Current.AliveContexts == 1 &&
		Current.AliveScopes == 1 &&
		Current.AliveServices == 1 &&
		Current.AlivePayloads == 1 &&
		Current.StaticMauiContextType == typeof(MauiContext).FullName;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"CompatibilityFormsStaticMauiContextRetentionLeakRepro",
			$"Window-scoped service payload: {FormatBytes(PayloadBytes)}",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  static Forms.MauiContext: {stats.StaticMauiContextType ?? "<null>"}",
			$"  static context resolves payload service: {stats.StaticMauiContextCanResolvePayload}",
			$"  MauiContexts alive after full GC: {stats.AliveContexts}/1",
			$"  service scopes alive after full GC: {stats.AliveScopes}/1",
			$"  scoped services alive after full GC: {stats.AliveServices}/1",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloads}/1",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)}");
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
	const int PayloadBytes = 80 * 1024 * 1024;

	public static async Task<ReproReport> RunAsync()
	{
		await Task.Yield();

		FormsReflection.ClearStaticContext();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: clear static Forms.MauiContext after window-scope initialization",
			clearStaticContextAfterInit: true);

		var current = await RunScenarioAsync(
			"current: static Forms.MauiContext keeps the last window-scoped context",
			clearStaticContextAfterInit: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(string name, bool clearStaticContextAfterInit)
	{
		FormsReflection.ClearStaticContext();
		ForceFullGc();

		var probe = CreateAndInitializeWindowScopedContext();

		if (clearStaticContextAfterInit)
			FormsReflection.ClearStaticContext();

		await Task.Yield();
		ForceFullGc();

		var staticContext = FormsReflection.GetStaticContext();
		var staticContextCanResolvePayload = TryResolvePayload(staticContext);
		var aliveContexts = probe.Context.TryGetTarget(out _) ? 1 : 0;
		var aliveScopes = probe.Scope.TryGetTarget(out _) ? 1 : 0;
		var aliveServices = probe.Service.TryGetTarget(out _) ? 1 : 0;
		var alivePayloads = probe.Payload.TryGetTarget(out _) ? 1 : 0;

		if (clearStaticContextAfterInit)
			FormsReflection.ClearStaticContext();

		return new RunStats(
			name,
			aliveContexts,
			aliveScopes,
			aliveServices,
			alivePayloads,
			alivePayloads * (long)PayloadBytes,
			staticContext?.GetType().FullName,
			staticContextCanResolvePayload);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static WindowScopeProbe CreateAndInitializeWindowScopedContext()
	{
		var services = new ServiceCollection();
		services.AddScoped(_ => new ScopedPayloadService(PayloadBytes));

		var rootProvider = services.BuildServiceProvider();
		var scope = rootProvider.CreateScope();
		var service = scope.ServiceProvider.GetRequiredService<ScopedPayloadService>();
		var mauiContext = new MauiContext(scope.ServiceProvider);
		var state = new ActivationState(mauiContext);

#pragma warning disable CS0612
		Forms.Init(state, new InitializationOptions { Flags = InitializationFlags.SkipRenderers });
#pragma warning restore CS0612

		scope.Dispose();
		rootProvider.Dispose();

		return new WindowScopeProbe(
			new WeakReference<MauiContext>(mauiContext),
			new WeakReference<IServiceScope>(scope),
			new WeakReference<ScopedPayloadService>(service),
			new WeakReference<byte[]>(service.Payload));
	}

	static bool TryResolvePayload(IMauiContext? context)
	{
		if (context == null)
			return false;

		try
		{
			return context.Services.GetService<ScopedPayloadService>() != null;
		}
		catch (ObjectDisposedException)
		{
			// Disposed scopes still retain resolved services even though service resolution is closed.
			return true;
		}
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}
}

internal sealed record WindowScopeProbe(
	WeakReference<MauiContext> Context,
	WeakReference<IServiceScope> Scope,
	WeakReference<ScopedPayloadService> Service,
	WeakReference<byte[]> Payload);

internal sealed class ScopedPayloadService
{
	public ScopedPayloadService(int byteCount)
	{
		Payload = new byte[byteCount];
		Payload[0] = 17;
		Payload[^1] = 29;
	}

	public byte[] Payload { get; }
}

internal static class FormsReflection
{
	static readonly FieldInfo MauiContextField =
		typeof(Forms).GetField("<MauiContext>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(Forms).FullName, "<MauiContext>k__BackingField");

	public static IMauiContext? GetStaticContext()
	{
		return MauiContextField.GetValue(null) as IMauiContext;
	}

	public static void ClearStaticContext()
	{
		MauiContextField.SetValue(null, null);
	}
}
