using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using ObjCRuntime;
using UIKit;

namespace IosShellItemRendererContextRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new RunnerPage());
	}
}

sealed class RunnerPage : ContentPage
{
	bool _ran;

	public RunnerPage()
	{
		Content = new Label
		{
			Text = "Running iOS ShellItemRenderer context retention repro...",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await TryRunAsync();
	}

	protected override async void OnHandlerChanged()
	{
		base.OnHandlerChanged();
		await TryRunAsync();
	}

	async Task TryRunAsync()
	{
		if (_ran || Handler?.MauiContext is not { } context)
			return;

		_ran = true;
		await Task.Delay(250);

		try
		{
			var report = await ReproSession.RunAsync(context);
			var text = report.ToText();
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(report.LeakProved ? 0 : 2);
		}
		catch (Exception ex)
		{
			var text = "IosShellItemRendererContextRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);

			await Task.Delay(250);
			Environment.Exit(1);
		}
	}
}

static class ReproSession
{
	public const string ResultsPath = "/tmp/ios-shellitemrenderer-context-retention-results.txt";

	const int Attempts = 96;
	const int PayloadBytes = 1024 * 1024;
	const int ShellSectionsPerItem = 3;

	static readonly FieldInfo ContextField =
		typeof(ShellItemRenderer).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(typeof(ShellItemRenderer).FullName, "_context");

	static readonly List<IReadOnlyList<ShellItemRenderer>> RetainedRendererSets = new();

	public static async Task<ReproReport> RunAsync(IMauiContext hostContext)
	{
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			hostContext,
			"control: ShellItemRenderer.Dispose() plus private _context clear",
			clearContextField: true);

		var current = await RunScenarioAsync(
			hostContext,
			"current: ShellItemRenderer.Dispose() only",
			clearContextField: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		GC.KeepAlive(RetainedRendererSets);
		return new ReproReport(Attempts, PayloadBytes, ShellSectionsPerItem, baseline, final, control, current);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext hostContext, string name, bool clearContextField)
	{
		var retainedRenderers = new List<ShellItemRenderer>(Attempts);
		var rendererRefs = new List<WeakReference<ShellItemRenderer>>(Attempts);
		var shellContextRefs = new List<WeakReference<PayloadShellContext>>(Attempts);
		var shellRefs = new List<WeakReference<Shell>>(Attempts);
		var shellItemRefs = new List<WeakReference<ShellItem>>(Attempts);
		var shellSectionRefs = new List<WeakReference<ShellSection>>(Attempts);
		var mauiContextRefs = new List<WeakReference<IMauiContext>>(Attempts);
		var providerRefs = new List<WeakReference<PayloadServiceProvider>>(Attempts);
		var payloadRefs = new List<PayloadWeakReferences>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedRenderer(
				hostContext,
				i,
				clearContextField,
				retainedRenderers,
				rendererRefs,
				shellContextRefs,
				shellRefs,
				shellItemRefs,
				shellSectionRefs,
				mauiContextRefs,
				providerRefs,
				payloadRefs);

			if (i % 12 == 0)
				await DrainMainQueueAsync();
		}

		RetainedRendererSets.Add(retainedRenderers);
		await DrainMainQueueAsync();
		ForceFullGc();
		GC.KeepAlive(retainedRenderers);

		return RunStats.From(
			name,
			rendererRefs,
			shellContextRefs,
			shellRefs,
			shellItemRefs,
			shellSectionRefs,
			mauiContextRefs,
			providerRefs,
			payloadRefs);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisposedRenderer(
		IMauiContext hostContext,
		int index,
		bool clearContextField,
		List<ShellItemRenderer> retainedRenderers,
		List<WeakReference<ShellItemRenderer>> rendererRefs,
		List<WeakReference<PayloadShellContext>> shellContextRefs,
		List<WeakReference<Shell>> shellRefs,
		List<WeakReference<ShellItem>> shellItemRefs,
		List<WeakReference<ShellSection>> shellSectionRefs,
		List<WeakReference<IMauiContext>> mauiContextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReferences> payloadRefs)
	{
		using var pool = new NSAutoreleasePool();

		var payload = new PayloadService(index, PayloadBytes);
		var provider = new PayloadServiceProvider(hostContext.Services, payload);
		var mauiContext = new MauiContext(provider);
		var shell = CreateShell(index, payload, out var shellItem, out var firstSection);
		var shellContext = new PayloadShellContext(shell);

		shell.Handler = new PayloadElementHandler(mauiContext);

		if (!ReferenceEquals(shell.Handler?.MauiContext, mauiContext))
			throw new InvalidOperationException("The Shell did not retain the expected payload MauiContext.");

		if (shell.Handler.MauiContext.Services.GetService(typeof(PayloadService)) is not PayloadService resolvedPayload ||
			!ReferenceEquals(resolvedPayload, payload))
		{
			throw new InvalidOperationException("The Shell MauiContext did not resolve the expected payload service.");
		}

		var renderer = new ShellItemRenderer(shellContext)
		{
			ShellItem = shellItem
		};

		rendererRefs.Add(new WeakReference<ShellItemRenderer>(renderer));
		shellContextRefs.Add(new WeakReference<PayloadShellContext>(shellContext));
		shellRefs.Add(new WeakReference<Shell>(shell));
		shellItemRefs.Add(new WeakReference<ShellItem>(shellItem));
		shellSectionRefs.Add(new WeakReference<ShellSection>(firstSection));
		mauiContextRefs.Add(new WeakReference<IMauiContext>(mauiContext));
		providerRefs.Add(new WeakReference<PayloadServiceProvider>(provider));
		payloadRefs.Add(new PayloadWeakReferences(new WeakReference<PayloadService>(payload), new WeakReference<byte[]>(payload.Bytes)));

		renderer.Dispose();

		if (clearContextField)
			ContextField.SetValue(renderer, null);

		retainedRenderers.Add(renderer);
	}

	static Shell CreateShell(int index, PayloadService payload, out ShellItem shellItem, out ShellSection firstSection)
	{
		var shell = new Shell
		{
			Title = $"Operations shell {index:000}",
			BindingContext = payload
		};

		var flyoutItem = new FlyoutItem
		{
			Title = $"Customer region {index:000}",
			BindingContext = payload
		};

		firstSection = null!;

		for (var sectionIndex = 0; sectionIndex < ShellSectionsPerItem; sectionIndex++)
		{
			var section = new ShellSection
			{
				Title = $"Work queue {sectionIndex + 1}",
				BindingContext = payload
			};

			var content = new ShellContent
			{
				Title = $"Open cases {index:000}-{sectionIndex + 1}",
				Content = new ContentPage
				{
					Title = $"Case board {index:000}-{sectionIndex + 1}",
					BindingContext = payload,
					Content = new Label { Text = $"Case payload {index:000}-{sectionIndex + 1}" }
				}
			};

			section.Items.Add(content);
			section.CurrentItem = content;
			flyoutItem.Items.Add(section);

			firstSection ??= section;
		}

		flyoutItem.CurrentItem = firstSection;
		shell.Items.Add(flyoutItem);
		shell.CurrentItem = flyoutItem;
		shellItem = flyoutItem;

		return shell;
	}

	static async Task DrainMainQueueAsync()
	{
		await Task.Delay(50);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.02));
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 5; i++)
		{
			using var pool = new NSAutoreleasePool();
			NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(75);
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

	internal sealed class PayloadShellContext : IShellContext
	{
		public PayloadShellContext(Shell shell)
		{
			Shell = shell;
		}

		public bool AllowFlyoutGesture => true;

		public IShellItemRenderer CurrentShellItemRenderer => null!;

		public Shell Shell { get; }

		public IShellPageRendererTracker CreatePageRendererTracker() => throw new NotSupportedException();

		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() => throw new NotSupportedException();

		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) => new TestShellSectionRenderer(shellSection);

		public IShellNavBarAppearanceTracker CreateNavBarAppearanceTracker() => throw new NotSupportedException();

		public IShellTabBarAppearanceTracker CreateTabBarAppearanceTracker() => new NoopShellTabBarAppearanceTracker();

		public IShellSearchResultsRenderer CreateShellSearchResultsRenderer() => throw new NotSupportedException();
	}

	sealed class TestShellSectionRenderer : IShellSectionRenderer
	{
		public TestShellSectionRenderer(ShellSection shellSection)
		{
			ShellSection = shellSection;
			ViewController = new UIViewController
			{
				Title = shellSection.Title
			};
		}

		public bool IsInMoreTab { get; set; }

		public ShellSection ShellSection { get; set; }

		public UIViewController ViewController { get; }

		public void Dispose()
		{
			ViewController.Dispose();
		}
	}

	sealed class NoopShellTabBarAppearanceTracker : IShellTabBarAppearanceTracker
	{
		public void ResetAppearance(UITabBarController controller)
		{
		}

		public void SetAppearance(UITabBarController controller, ShellAppearance appearance)
		{
		}

		public void UpdateLayout(UITabBarController controller)
		{
		}

		public void Dispose()
		{
		}
	}

	sealed class PayloadElementHandler : IViewHandler
	{
		public PayloadElementHandler(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public object? PlatformView => null;

		public Microsoft.Maui.IElement? VirtualView { get; private set; }

		IView? IViewHandler.VirtualView => VirtualView as IView;

		public IMauiContext? MauiContext { get; private set; }

		public bool HasContainer { get; set; }

		public object? ContainerView => null;

		public void SetMauiContext(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public void SetVirtualView(Microsoft.Maui.IElement view)
		{
			VirtualView = view;
		}

		public void UpdateValue(string property)
		{
		}

		public void Invoke(string command, object? args = null)
		{
		}

		public void DisconnectHandler()
		{
			VirtualView = null;
		}

		public Microsoft.Maui.Graphics.Size GetDesiredSize(double widthConstraint, double heightConstraint)
		{
			return new Microsoft.Maui.Graphics.Size(widthConstraint, heightConstraint);
		}

		public void PlatformArrange(Microsoft.Maui.Graphics.Rect frame)
		{
		}
	}

	internal sealed class PayloadServiceProvider : IServiceProvider, IKeyedServiceProvider
	{
		readonly IServiceProvider _inner;

		public PayloadServiceProvider(IServiceProvider inner, PayloadService payload)
		{
			_inner = inner;
			Payload = payload;
		}

		public PayloadService Payload { get; }

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(PayloadService))
				return Payload;

			return _inner.GetService(serviceType);
		}

		public object? GetKeyedService(Type serviceType, object? serviceKey)
		{
			if (serviceType == typeof(PayloadService))
				return Payload;

			return _inner is IKeyedServiceProvider keyedProvider
				? keyedProvider.GetKeyedService(serviceType, serviceKey)
				: null;
		}

		public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
		{
			if (serviceType == typeof(PayloadService))
				return Payload;

			if (_inner is IKeyedServiceProvider keyedProvider)
				return keyedProvider.GetRequiredKeyedService(serviceType, serviceKey);

			throw new InvalidOperationException($"No keyed service provider is available for {serviceType}.");
		}
	}

	internal sealed class PayloadService
	{
		public PayloadService(int index, int bytes)
		{
			Index = index;
			Bytes = new byte[bytes];

			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = unchecked((byte)(index + i));
		}

		public int Index { get; }

		public byte[] Bytes { get; }
	}

	internal readonly record struct PayloadWeakReferences(
		WeakReference<PayloadService> Payload,
		WeakReference<byte[]> Bytes);

	internal readonly record struct RunStats(
		string Name,
		int Attempts,
		int AliveRenderers,
		int AliveShellContexts,
		int AliveShells,
		int AliveShellItems,
		int AliveShellSections,
		int AliveMauiContexts,
		int AliveProviders,
		int AlivePayloadServices,
		int AlivePayloadByteArrays,
		int RenderersWithContext,
		int RenderersResolvingPayloadService,
		long RetainedPayloadBytes)
	{
		internal static RunStats From(
			string name,
			IReadOnlyList<WeakReference<ShellItemRenderer>> rendererRefs,
			IReadOnlyList<WeakReference<PayloadShellContext>> shellContextRefs,
			IReadOnlyList<WeakReference<Shell>> shellRefs,
			IReadOnlyList<WeakReference<ShellItem>> shellItemRefs,
			IReadOnlyList<WeakReference<ShellSection>> shellSectionRefs,
			IReadOnlyList<WeakReference<IMauiContext>> mauiContextRefs,
			IReadOnlyList<WeakReference<PayloadServiceProvider>> providerRefs,
			IReadOnlyList<PayloadWeakReferences> payloadRefs)
		{
			var renderersWithContext = 0;
			var renderersResolvingPayloadService = 0;

			foreach (var rendererRef in rendererRefs)
			{
				if (!rendererRef.TryGetTarget(out var renderer))
					continue;

				if (ContextField.GetValue(renderer) is PayloadShellContext context)
				{
					renderersWithContext++;

					if (context.Shell.Handler?.MauiContext?.Services.GetService(typeof(PayloadService)) is PayloadService)
						renderersResolvingPayloadService++;
				}
			}

			var alivePayloadServices = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
			var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));

			return new RunStats(
				name,
				rendererRefs.Count,
				CountAlive(rendererRefs),
				CountAlive(shellContextRefs),
				CountAlive(shellRefs),
				CountAlive(shellItemRefs),
				CountAlive(shellSectionRefs),
				CountAlive(mauiContextRefs),
				CountAlive(providerRefs),
				alivePayloadServices,
				alivePayloadByteArrays,
				renderersWithContext,
				renderersResolvingPayloadService,
				(long)alivePayloadByteArrays * PayloadBytes);
		}
	}
}

internal readonly record struct ReproReport(
	int Attempts,
	int PayloadBytes,
	int ShellSectionsPerItem,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.RunStats Control,
	ReproSession.RunStats Current)
{
	public bool LeakProved =>
		Control.AliveRenderers == Attempts &&
		Control.RenderersWithContext == 0 &&
		Control.RenderersResolvingPayloadService == 0 &&
		Control.AliveShellContexts <= 1 &&
		Control.AliveShells <= 1 &&
		Control.AliveShellItems <= 1 &&
		Control.AliveShellSections <= 1 &&
		Control.AliveMauiContexts <= 1 &&
		Control.AliveProviders <= 1 &&
		Control.AlivePayloadServices <= 1 &&
		Control.AlivePayloadByteArrays <= 1 &&
		Current.AliveRenderers == Attempts &&
		Current.RenderersWithContext == Attempts &&
		Current.RenderersResolvingPayloadService == Attempts &&
		Current.AliveShellContexts == Attempts &&
		Current.AliveShells == Attempts &&
		Current.AliveShellItems == Attempts &&
		Current.AliveShellSections == Attempts &&
		Current.AliveMauiContexts == Attempts &&
		Current.AliveProviders == Attempts &&
		Current.AlivePayloadServices == Attempts &&
		Current.AlivePayloadByteArrays == Attempts;

	public string ToText()
	{
		var builder = new StringBuilder();
		builder.AppendLine("IosShellItemRendererContextRetentionRepro");
		builder.AppendLine($"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
		builder.AppendLine($"ShellItemRenderer peers retained per scenario: {Attempts}");
		builder.AppendLine($"Shell sections per item: {ShellSectionsPerItem}");
		builder.AppendLine($"Payload per Shell MauiContext: {PayloadBytes / 1024d / 1024d:0.0} MiB");
		builder.AppendLine($"Baseline managed heap: {BaselineManagedBytes:N0} bytes");
		builder.AppendLine($"Final managed heap: {FinalManagedBytes:N0} bytes");
		builder.AppendLine($"Managed heap delta: {(FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d:0.0} MiB");
		builder.AppendLine();
		AppendScenario(builder, Control);
		builder.AppendLine();
		AppendScenario(builder, Current);
		builder.AppendLine();
		builder.AppendLine($"Control retained payload: {Control.RetainedPayloadBytes / 1024d / 1024d:0.0} MiB");
		builder.AppendLine($"Current retained payload: {Current.RetainedPayloadBytes / 1024d / 1024d:0.0} MiB");
		builder.AppendLine("Leak path: retained disposed ShellItemRenderer native peer -> readonly _context -> Shell -> Handler.MauiContext -> service provider -> payload service/byte array.");
		builder.AppendLine("Distinct from root ShellRenderer disposal leaks: this proof retains only disposed ShellItemRenderer peers and the control runs the same dispose path before clearing just _context.");
		builder.AppendLine($"dotnet-version: {Environment.Version}");
		return builder.ToString();
	}

	static void AppendScenario(StringBuilder builder, ReproSession.RunStats stats)
	{
		builder.AppendLine($"Run: {stats.Name}");
		builder.AppendLine($"  disposed ShellItemRenderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}");
		builder.AppendLine($"  renderer _context fields alive: {stats.RenderersWithContext}/{stats.Attempts}");
		builder.AppendLine($"  renderers resolving payload service through Shell.Handler.MauiContext: {stats.RenderersResolvingPayloadService}/{stats.Attempts}");
		builder.AppendLine($"  Shell contexts alive after full GC: {stats.AliveShellContexts}/{stats.Attempts}");
		builder.AppendLine($"  Shells alive after full GC: {stats.AliveShells}/{stats.Attempts}");
		builder.AppendLine($"  ShellItems alive after full GC: {stats.AliveShellItems}/{stats.Attempts}");
		builder.AppendLine($"  first ShellSections alive after full GC: {stats.AliveShellSections}/{stats.Attempts}");
		builder.AppendLine($"  MauiContexts alive after full GC: {stats.AliveMauiContexts}/{stats.Attempts}");
		builder.AppendLine($"  payload service providers alive after full GC: {stats.AliveProviders}/{stats.Attempts}");
		builder.AppendLine($"  payload services alive after full GC: {stats.AlivePayloadServices}/{stats.Attempts}");
		builder.AppendLine($"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}");
		builder.AppendLine($"  retained payload bytes: {stats.RetainedPayloadBytes:N0}");
	}
}
