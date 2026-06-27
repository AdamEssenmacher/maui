#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace AndroidModalNavigationManagerHandlerChangedLeakRepro;

public static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		var control = await RunScenarioAsync(mauiContext, useCapturedPageToken: true);
		var current = await RunScenarioAsync(mauiContext, useCapturedPageToken: false);

		return new ReproReport(control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(IMauiContext mauiContext, bool useCapturedPageToken)
	{
		var windows = new List<WeakReference>(Attempts);
		var managers = new List<WeakReference>(Attempts);
		var rootPages = new List<WeakReference>(Attempts);
		var payloads = new List<WeakReference>(Attempts);
		var payloadArrays = new List<WeakReference>(Attempts);
		var retainedOldPages = new List<Page>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			var attempt = await CreateAttemptAsync(mauiContext, useCapturedPageToken);
			windows.Add(attempt.Window);
			managers.Add(attempt.Manager);
			rootPages.Add(attempt.RootPage);
			payloads.Add(attempt.Payload);
			payloadArrays.Add(attempt.PayloadBytes);
			retainedOldPages.Add(attempt.RetainedOldPage);
		}

		await ForceFullGcAsync();

		return new ScenarioResult(
			useCapturedPageToken ? "control: captured-page unsubscribe token" : "current: token re-evaluates CurrentPlatformPage",
			CountAlive(windows),
			CountAlive(managers),
			CountAlive(rootPages),
			CountAlive(payloads),
			CountAlive(payloadArrays),
			retainedOldPages.Count);
	}

	static async Task<AttemptRefs> CreateAttemptAsync(IMauiContext mauiContext, bool useCapturedPageToken)
	{
		var payload = new Payload(PayloadBytes);
		var rootPage = new ContentPage
		{
			BindingContext = payload,
			Content = new Label { Text = "root" }
		};
		var retainedOldPage = new ContentPage();
		var window = new Window(rootPage);
		var manager = GetModalNavigationManager(window);

		rootPage.Handler = new StubElementHandler(rootPage, mauiContext);
		SetWindowActivated(window);
		SetPlatformModalPages(manager, retainedOldPage);

		await InvokeSyncWhenReadyAsync(manager);

		if (useCapturedPageToken)
			ReplaceCurrentTokenWithCapturedPageToken(manager, retainedOldPage);

		SetPlatformModalPages(manager);
		InvokeDisconnectPlatformPageWatchingForLoaded(manager);

		var result = new AttemptRefs(
			new WeakReference(window),
			new WeakReference(manager),
			new WeakReference(rootPage),
			new WeakReference(payload),
			new WeakReference(payload.Bytes),
			retainedOldPage);

		window = null!;
		manager = null!;
		rootPage = null!;
		payload = null!;

		return result;
	}

	static object GetModalNavigationManager(Window window)
	{
		var property = typeof(Window).GetProperty("ModalNavigationManager", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingMemberException(typeof(Window).FullName, "ModalNavigationManager");

		return property.GetValue(window)
			?? throw new InvalidOperationException("Window.ModalNavigationManager was null.");
	}

	static void SetWindowActivated(Window window)
	{
		var property = typeof(Window).GetProperty(nameof(Window.IsActivated), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new MissingMemberException(typeof(Window).FullName, nameof(Window.IsActivated));

		property.SetValue(window, true);
	}

	static void SetPlatformModalPages(object manager, params Page[] pages)
	{
		var field = manager.GetType().GetField("_platformModalPages", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(manager.GetType().FullName, "_platformModalPages");

		var list = (List<Page>)field.GetValue(manager)!;
		list.Clear();
		list.AddRange(pages);
	}

	static async Task InvokeSyncWhenReadyAsync(object manager)
	{
		var method = manager.GetType().GetMethod("SyncModalStackWhenPlatformIsReadyAsync", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingMethodException(manager.GetType().FullName, "SyncModalStackWhenPlatformIsReadyAsync");

		var task = (Task)method.Invoke(manager, Array.Empty<object>())!;
		await task.ConfigureAwait(false);
	}

	static void InvokeDisconnectPlatformPageWatchingForLoaded(object manager)
	{
		var method = manager.GetType().GetMethod("DisconnectPlatformPageWatchingForLoaded", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingMethodException(manager.GetType().FullName, "DisconnectPlatformPageWatchingForLoaded");

		method.Invoke(manager, Array.Empty<object>());
	}

	static void ReplaceCurrentTokenWithCapturedPageToken(object manager, Page originalPage)
	{
		var handler = CreatePlatformPageHandlerChangedDelegate(manager);
		originalPage.HandlerChanged -= handler;
		originalPage.HandlerChanged += handler;

		var field = manager.GetType().GetField("_platformPageWatchingForLoaded", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(manager.GetType().FullName, "_platformPageWatchingForLoaded");

		field.SetValue(manager, new CapturedPageUnsubscribeToken(originalPage, handler));
	}

	static EventHandler CreatePlatformPageHandlerChangedDelegate(object manager)
	{
		var method = manager.GetType().GetMethod("OnCurrentPlatformPageHandlerChanged", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingMethodException(manager.GetType().FullName, "OnCurrentPlatformPageHandlerChanged");

		return (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), manager, method);
	}

	static async Task ForceFullGcAsync()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			await Task.Delay(50);
		}
	}

	static int CountAlive(List<WeakReference> weakReferences)
	{
		var count = 0;
		foreach (var weakReference in weakReferences)
		{
			if (weakReference.IsAlive)
				count++;
		}

		return count;
	}

	sealed class CapturedPageUnsubscribeToken(Page page, EventHandler handler) : IDisposable
	{
		Page? _page = page;
		EventHandler? _handler = handler;

		public void Dispose()
		{
			if (_page is not null && _handler is not null)
				_page.HandlerChanged -= _handler;

			_page = null;
			_handler = null;
		}
	}

	sealed class StubElementHandler : IViewHandler
	{
		public StubElementHandler(IElement view, IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
			VirtualView = (IView)view;
			PlatformView = new object();
		}

		public object? PlatformView { get; private set; }

		public IView? VirtualView { get; private set; }

		IElement? IElementHandler.VirtualView => VirtualView;

		public IMauiContext? MauiContext { get; private set; }

		public bool HasContainer { get; set; }

		public object? ContainerView => null;

		public void SetMauiContext(IMauiContext mauiContext) =>
			MauiContext = mauiContext;

		public void SetVirtualView(IElement view) =>
			VirtualView = (IView)view;

		public void UpdateValue(string property)
		{
		}

		public void Invoke(string command, object? args)
		{
		}

		public void DisconnectHandler()
		{
			if (VirtualView?.Handler == this)
				VirtualView.Handler = null;

			VirtualView = null;
			PlatformView = null;
		}

		public Size GetDesiredSize(double widthConstraint, double heightConstraint) =>
			Size.Zero;

		public void PlatformArrange(Rect frame)
		{
		}
	}

	sealed class Payload
	{
		public Payload(int size)
		{
			Bytes = new byte[size];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = 0x5a;
		}

		public byte[] Bytes { get; }
	}

	readonly record struct AttemptRefs(
		WeakReference Window,
		WeakReference Manager,
		WeakReference RootPage,
		WeakReference Payload,
		WeakReference PayloadBytes,
		Page RetainedOldPage);
}

public sealed record ScenarioResult(
	string Name,
	int WindowsAlive,
	int ManagersAlive,
	int RootPagesAlive,
	int PayloadsAlive,
	int PayloadByteArraysAlive,
	int RetainedOldPages);

public sealed record ReproReport(ScenarioResult Control, ScenarioResult Current)
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	public bool LeakProved =>
		Control.PayloadByteArraysAlive == 0 &&
		Current.PayloadByteArraysAlive == Attempts;

	public string ToText()
	{
		var builder = new StringBuilder();
		builder.AppendLine("AndroidModalNavigationManagerHandlerChangedLeakRepro");
		builder.AppendLine($"Attempts: {Attempts}");
		builder.AppendLine($"Payload per attempt: {FormatBytes(PayloadBytes)}");
		builder.AppendLine($"Leak proved: {LeakProved}");
		builder.AppendLine();
		AppendScenario(builder, Control);
		builder.AppendLine();
		AppendScenario(builder, Current);
		builder.AppendLine();
		builder.AppendLine($"Current retained payload bytes: {FormatBytes(Current.PayloadByteArraysAlive * (long)PayloadBytes)}");
		return builder.ToString();
	}

	static void AppendScenario(StringBuilder builder, ScenarioResult result)
	{
		builder.AppendLine($"Run: {result.Name}");
		builder.AppendLine($"  app-retained old pages: {result.RetainedOldPages}/{Attempts}");
		builder.AppendLine($"  windows alive after full GC: {result.WindowsAlive}/{Attempts}");
		builder.AppendLine($"  modal managers alive after full GC: {result.ManagersAlive}/{Attempts}");
		builder.AppendLine($"  root pages alive after full GC: {result.RootPagesAlive}/{Attempts}");
		builder.AppendLine($"  payloads alive after full GC: {result.PayloadsAlive}/{Attempts}");
		builder.AppendLine($"  payload byte arrays alive after full GC: {result.PayloadByteArraysAlive}/{Attempts}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024L * 1024 * 1024)
			return $"{bytes / (1024d * 1024 * 1024):0.0} GiB";
		if (bytes >= 1024L * 1024)
			return $"{bytes / (1024d * 1024):0.0} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024d:0.0} KiB";
		return $"{bytes} B";
	}
}
