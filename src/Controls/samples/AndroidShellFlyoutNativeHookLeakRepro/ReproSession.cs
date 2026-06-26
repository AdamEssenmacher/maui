using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Android.Content;
using Android.Widget;
using AndroidX.AppCompat.Widget;
using AndroidX.DrawerLayout.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using AView = Android.Views.View;
using ViewGroup = Android.Views.ViewGroup;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;

namespace AndroidShellFlyoutNativeHookLeakRepro;

static class ReproSession
{
	const int CycleCount = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly MethodInfo OnFlyoutStateChangingMethod =
		typeof(ShellFlyoutTemplatedContentRenderer).GetMethod(
			"OnFlyoutStateChanging",
			BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(
			nameof(ShellFlyoutTemplatedContentRenderer),
			"OnFlyoutStateChanging");

	static readonly FieldInfo RootViewField =
		typeof(ShellFlyoutTemplatedContentRenderer).GetField(
			"_rootView",
			BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(
			nameof(ShellFlyoutTemplatedContentRenderer),
			"_rootView");

	static readonly FieldInfo FlyoutContentViewField =
		typeof(ShellFlyoutTemplatedContentRenderer).GetField(
			"_flyoutContentView",
			BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(
			nameof(ShellFlyoutTemplatedContentRenderer),
			"_flyoutContentView");

	public static ReproReport Run(IMauiContext mauiContext, Shell shell)
	{
		ForceFullCollection();

		var control = RunScenario(
			mauiContext,
			shell,
			"control: complete delayed layout cleanup, retain DrawerLayout roots, then detach the drawer-state hook",
			applyExpectedCleanup: true);

		var currentDispose = RunScenario(
			mauiContext,
			shell,
			"current dispose: complete delayed layout cleanup, then dispose before the drawer opens",
			applyExpectedCleanup: false);

		return new ReproReport(control, currentDispose);
	}

	static ScenarioResult RunScenario(
		IMauiContext mauiContext,
		Shell shell,
		string name,
		bool applyExpectedCleanup)
	{
		var retainedRoots = new RetainedNativeRoots();
		var rendererReferences = new List<WeakReference<PayloadShellFlyoutTemplatedContentRenderer>>();
		var payloadReferences = new List<WeakReference<LeakPayload>>();

		for (var i = 0; i < CycleCount; i++)
		{
			CreateAndDisposeRenderer(
				mauiContext,
				shell,
				retainedRoots,
				rendererReferences,
				payloadReferences,
				applyExpectedCleanup,
				i);
		}

		ForceFullCollection();

		var result = new ScenarioResult(
			name,
			RendererAlive: CountAlive(rendererReferences),
			PayloadAlive: CountAlive(payloadReferences),
			RetainedDrawerLayouts: retainedRoots.DrawerLayouts.Count);

		GC.KeepAlive(retainedRoots);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateAndDisposeRenderer(
		IMauiContext mauiContext,
		Shell shell,
		RetainedNativeRoots retainedRoots,
		List<WeakReference<PayloadShellFlyoutTemplatedContentRenderer>> rendererReferences,
		List<WeakReference<LeakPayload>> payloadReferences,
		bool applyExpectedCleanup,
		int index)
	{
		var payload = new LeakPayload(index, PayloadBytes);
		var shellContext = new ReproShellContext(mauiContext, shell);
		var renderer = new PayloadShellFlyoutTemplatedContentRenderer(shellContext, payload);
		var rootView = renderer.AndroidView;

		retainedRoots.DrawerLayouts.Add(shellContext.CurrentDrawerLayout);

		DispatchGlobalLayouts(rootView, count: 2);

		if (applyExpectedCleanup)
			RemoveDrawerStateChangedHandler(renderer, shellContext.CurrentDrawerLayout);

		rendererReferences.Add(new WeakReference<PayloadShellFlyoutTemplatedContentRenderer>(renderer));
		payloadReferences.Add(new WeakReference<LeakPayload>(payload));

		renderer.Dispose();
	}

	static void DispatchGlobalLayouts(AView rootView, int count)
	{
		for (var i = 0; i < count; i++)
			rootView.ViewTreeObserver?.DispatchOnGlobalLayout();
	}

	static void RemoveDrawerStateChangedHandler(
		ShellFlyoutTemplatedContentRenderer renderer,
		DrawerLayout drawerLayout)
	{
		var handler = (EventHandler<DrawerLayout.DrawerStateChangedEventArgs>)Delegate.CreateDelegate(
			typeof(EventHandler<DrawerLayout.DrawerStateChangedEventArgs>),
			renderer,
			OnFlyoutStateChangingMethod);

		drawerLayout.DrawerStateChanged -= handler;
	}

	static int CountAlive<T>(List<WeakReference<T>> references)
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

	static void ForceFullCollection()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Java.Lang.JavaSystem.RunFinalization();
			Thread.Sleep(50);
		}
	}

	sealed class ReproShellContext : IShellContext
	{
		public ReproShellContext(IMauiContext mauiContext, Shell shell)
		{
			AndroidContext = mauiContext.Context
				?? throw new InvalidOperationException("Android context is required.");
			CurrentDrawerLayout = new DrawerLayout(AndroidContext);
			Shell = shell;
		}

		public Context AndroidContext { get; }

		public DrawerLayout CurrentDrawerLayout { get; }

		public Shell Shell { get; }

		public IShellObservableFragment CreateFragmentForPage(Page page) => throw new NotSupportedException();

		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() => throw new NotSupportedException();

		public IShellItemRenderer CreateShellItemRenderer(ShellItem shellItem) => throw new NotSupportedException();

		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) => throw new NotSupportedException();

		public IShellToolbarTracker CreateTrackerForToolbar(AToolbar toolbar) => throw new NotSupportedException();

		public IShellToolbarAppearanceTracker CreateToolbarAppearanceTracker() => throw new NotSupportedException();

		public IShellTabLayoutAppearanceTracker CreateTabLayoutAppearanceTracker(ShellSection shellSection) => throw new NotSupportedException();

		public IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) => throw new NotSupportedException();
	}

	sealed class PayloadShellFlyoutTemplatedContentRenderer : ShellFlyoutTemplatedContentRenderer
	{
		public PayloadShellFlyoutTemplatedContentRenderer(IShellContext shellContext, LeakPayload payload)
			: base(shellContext)
		{
			Payload = payload;
		}

		public LeakPayload? Payload { get; private set; }

		protected override void UpdateFlyoutContent()
		{
			if (FlyoutContentViewField.GetValue(this) is not null)
				return;

			if (RootViewField.GetValue(this) is not ViewGroup rootView)
				return;

			var context = rootView.Context
				?? throw new InvalidOperationException("Root view context is required.");
			var dummyFlyoutContent = new FrameLayout(context)
			{
				LayoutParameters = new ViewGroup.LayoutParams(1, 1)
			};

			FlyoutContentViewField.SetValue(this, dummyFlyoutContent);
			rootView.AddView(dummyFlyoutContent);
		}
	}

	sealed class LeakPayload
	{
		public LeakPayload(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			Bytes[0] = (byte)(id % 251);
			Bytes[^1] = (byte)((id * 17) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}

	sealed class RetainedNativeRoots
	{
		public List<DrawerLayout> DrawerLayouts { get; } = new();
	}

	public sealed record ScenarioResult(
		string Name,
		int RendererAlive,
		int PayloadAlive,
		int RetainedDrawerLayouts)
	{
		public long PayloadBytesRetained => (long)PayloadAlive * PayloadBytes;
	}

	public sealed record ReproReport(ScenarioResult Control, ScenarioResult CurrentDispose)
	{
		public bool LeakProved =>
			Control.PayloadAlive == 0 &&
			Control.RendererAlive == 0 &&
			CurrentDispose.PayloadAlive >= CycleCount * 9 / 10 &&
			CurrentDispose.RendererAlive >= CycleCount * 9 / 10;

		public string ToText()
		{
			var builder = new StringBuilder();
			builder.AppendLine("Android ShellFlyoutTemplatedContentRenderer native-hook leak repro");
			builder.AppendLine($"Cycles: {CycleCount}");
			builder.AppendLine($"Payload per renderer: {PayloadBytes / 1024 / 1024} MiB");
			builder.AppendLine($"Leak proved: {LeakProved}");
			builder.AppendLine();
			AppendScenario(builder, Control);
			builder.AppendLine();
			AppendScenario(builder, CurrentDispose);
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine(result.Name);
			builder.AppendLine($"  retained native drawer layouts: {result.RetainedDrawerLayouts}/{CycleCount}");
			builder.AppendLine($"  disposed renderers alive after full GC: {result.RendererAlive}/{CycleCount}");
			builder.AppendLine($"  renderer payloads alive after full GC: {result.PayloadAlive}/{CycleCount}");
			builder.AppendLine($"  retained payload bytes: {result.PayloadBytesRetained:N0}");
		}
	}
}
