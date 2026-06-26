using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;
using AView = Android.Views.View;

namespace AndroidDragDropLocalStateRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AlivePayloads,
	int AliveVirtualViews,
	int AliveDataPackages,
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
		Control.AlivePayloads == 0 &&
		Control.AliveDataPackages == 0 &&
		Current.AlivePayloads == Attempts &&
		Current.AliveDataPackages == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidDragDropLocalStateRetentionLeakRepro",
			$"Attempts: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
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
			$"  retained native platform views: {stats.Attempts}",
			$"  virtual views alive after full GC: {stats.AliveVirtualViews}/{stats.Attempts}",
			$"  data packages alive after full GC: {stats.AliveDataPackages}/{stats.Attempts}",
			$"  drag local-state payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
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

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	public static async Task<ReproReport> RunAsync()
	{
		await Task.Yield();

		var mauiContext = Application.Current?.Windows.FirstOrDefault()?.Page?.Handler?.MauiContext
			?? throw new InvalidOperationException("MauiContext is not available.");

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: dispose drag/drop handler before GesturePlatformManager disposal",
			mauiContext,
			disposeDragHandler: true);

		var current = await RunScenarioAsync(
			"current: GesturePlatformManager disposal leaves native drag listener and local state",
			mauiContext,
			disposeDragHandler: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(string name, IMauiContext mauiContext, bool disposeDragHandler)
	{
		var payloadRefs = new List<WeakReference<Payload>>(Attempts);
		var virtualViewRefs = new List<WeakReference<View>>(Attempts);
		var dataPackageRefs = new List<WeakReference<DataPackage>>(Attempts);
		var retainedPlatformViews = new List<AView>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedDropView(mauiContext, disposeDragHandler, payloadRefs, virtualViewRefs, dataPackageRefs, retainedPlatformViews, i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		var alivePayloads = payloadRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveViews = virtualViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePackages = dataPackageRefs.Count(static wr => wr.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			alivePayloads,
			aliveViews,
			alivePackages,
			(long)alivePayloads * PayloadBytes);
	}

	static void CreateDisposedDropView(
		IMauiContext mauiContext,
		bool disposeDragHandler,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<View>> virtualViewRefs,
		List<WeakReference<DataPackage>> dataPackageRefs,
		List<AView> retainedPlatformViews,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		payloadRefs.Add(new WeakReference<Payload>(payload));

		var dataPackage = new DataPackage
		{
			Text = $"drag-payload-{index}"
		};
		dataPackage.Properties.Add("payload", payload);
		dataPackageRefs.Add(new WeakReference<DataPackage>(dataPackage));

		var view = new Border
		{
			WidthRequest = 64,
			HeightRequest = 64,
			BackgroundColor = Colors.CornflowerBlue
		};
		view.GestureRecognizers.Add(new DropGestureRecognizer { AllowDrop = true });
		virtualViewRefs.Add(new WeakReference<View>(view));

		var platformView = (AView)view.ToPlatform(mauiContext);
		var gesturePlatformManager = GetGesturePlatformManager(view);
		var dragDropHandler = GetDragDropGestureHandler(gesturePlatformManager);

		SeedInterruptedDragLocalState(dragDropHandler, platformView, view, dataPackage);

		if (disposeDragHandler)
			((IDisposable)dragDropHandler).Dispose();

		((IDisposable)gesturePlatformManager).Dispose();

		view.GestureRecognizers.Clear();
		view.Handler?.DisconnectHandler();
		view.Handler = null;

		retainedPlatformViews.Add(platformView);
	}

	static object GetGesturePlatformManager(View view)
	{
		var gestureManagerField = typeof(View).GetField("_gestureManager", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(View).FullName, "_gestureManager");
		var gestureManager = gestureManagerField.GetValue(view)
			?? throw new InvalidOperationException("GestureManager was not created.");
		var property = gestureManager.GetType().GetProperty("GesturePlatformManager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new MissingMemberException(gestureManager.GetType().FullName, "GesturePlatformManager");
		return property.GetValue(gestureManager)
			?? throw new InvalidOperationException("GesturePlatformManager was not connected.");
	}

	static object GetDragDropGestureHandler(object gesturePlatformManager)
	{
		var field = gesturePlatformManager.GetType().GetField("_dragAndDropGestureHandler", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(gesturePlatformManager.GetType().FullName, "_dragAndDropGestureHandler");
		var lazy = field.GetValue(gesturePlatformManager)
			?? throw new InvalidOperationException("Drag/drop Lazy was not created.");
		var valueProperty = lazy.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public)
			?? throw new MissingMemberException(lazy.GetType().FullName, "Value");
		return valueProperty.GetValue(lazy)
			?? throw new InvalidOperationException("Drag/drop handler was not created.");
	}

	static void SeedInterruptedDragLocalState(object dragDropHandler, AView platformView, View view, DataPackage dataPackage)
	{
		var localStateType = dragDropHandler.GetType().GetNestedType("CustomLocalStateData", BindingFlags.NonPublic)
			?? throw new MissingMemberException(dragDropHandler.GetType().FullName, "CustomLocalStateData");
		var localState = Activator.CreateInstance(localStateType)
			?? throw new InvalidOperationException("Could not create drag local state.");

		SetProperty(localState, "SourcePlatformView", platformView);
		SetProperty(localState, "SourceElement", view);
		SetProperty(localState, "DataPackage", dataPackage);
		SetProperty(localState, "AcceptedOperation", DataPackageOperation.Copy);

		var field = dragDropHandler.GetType().GetField("_currentCustomLocalStateData", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(dragDropHandler.GetType().FullName, "_currentCustomLocalStateData");
		field.SetValue(dragDropHandler, localState);
	}

	static void SetProperty(object target, string propertyName, object value)
	{
		var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new MissingMemberException(target.GetType().FullName, propertyName);
		property.SetValue(target, value);
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

	sealed class Payload
	{
		public Payload(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			Bytes[0] = (byte)(id % 251);
			Bytes[^1] = (byte)((id + 1) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}
}
