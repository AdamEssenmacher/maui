#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AView = Android.Views.View;

namespace AndroidPlatformViewOwnerRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveNativeViews,
	int AliveHandlers,
	int AliveVirtualViews,
	int AlivePayloads,
	int AlivePayloadByteArrays,
	long RetainedPayloadBytes,
	IReadOnlyList<ControlStats> Controls);

public sealed record ControlStats(
	string Control,
	int Attempts,
	int AliveVirtualViews,
	int AlivePayloadByteArrays);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveVirtualViews == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveVirtualViews == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadByteArrays == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidPlatformViewOwnerRetentionLeakRepro",
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
			$"  retained native platform views: {stats.AliveNativeViews}/{stats.Attempts}",
			$"  handlers alive after full GC: {stats.AliveHandlers}/{stats.Attempts}",
			$"  virtual views alive after full GC: {stats.AliveVirtualViews}/{stats.Attempts}",
			$"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)",
			$"  per control: {string.Join(", ", stats.Controls.Select(c => $"{c.Control} {c.AlivePayloadByteArrays}/{c.Attempts}"))}");
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
	const int IterationsPerControl = 16;
	const int PayloadBytes = 1024 * 1024;

	static readonly List<AView> RetainedNativeViews = new();

	static readonly ControlCase[] Cases =
	{
		new("Layout", CreateLayoutCase),
		new("Border", CreateBorderCase),
		new("ScrollView", CreateScrollViewCase),
		new("RefreshView", CreateRefreshViewCase),
		new("SwipeView", CreateSwipeViewCase)
	};

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		RetainedNativeViews.Clear();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: disconnect then clear native owner fields",
			clearOwnerFields: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: disconnect leaves native owner fields assigned",
			clearOwnerFields: false);

		ForceFullGc();
		GC.KeepAlive(RetainedNativeViews);
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Cases.Length * IterationsPerControl, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool clearOwnerFields)
	{
		var nativeViewRefs = new List<WeakReference<AView>>();
		var handlerRefs = new List<WeakReference<IElementHandler>>();
		var virtualViewRefs = new List<VirtualViewWeakReference>();
		var payloadRefs = new List<PayloadWeakReference>();

		foreach (var controlCase in Cases)
		{
			for (var i = 0; i < IterationsPerControl; i++)
			{
				CreateDisconnectedPlatformView(
					mauiContext,
					controlCase,
					clearOwnerFields,
					nativeViewRefs,
					handlerRefs,
					virtualViewRefs,
					payloadRefs,
					i);

				if (i % 4 == 0)
					await Task.Yield();
			}
		}

		ForceFullGc();
		GC.KeepAlive(RetainedNativeViews);

		var aliveNativeViews = nativeViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveHandlers = handlerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveVirtualViews = virtualViewRefs.Count(static wr => wr.View.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var controlStats = Cases.Select(controlCase =>
		{
			var views = virtualViewRefs.Where(wr => wr.Control == controlCase.Name).ToArray();
			var payloads = payloadRefs.Where(wr => wr.Control == controlCase.Name).ToArray();
			return new ControlStats(
				controlCase.Name,
				views.Length,
				views.Count(static wr => wr.View.TryGetTarget(out _)),
				payloads.Count(static wr => wr.Bytes.TryGetTarget(out _)));
		}).ToArray();

		return new RunStats(
			name,
			Cases.Length * IterationsPerControl,
			aliveNativeViews,
			aliveHandlers,
			aliveVirtualViews,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes,
			controlStats);
	}

	static void CreateDisconnectedPlatformView(
		IMauiContext mauiContext,
		ControlCase controlCase,
		bool clearOwnerFields,
		List<WeakReference<AView>> nativeViewRefs,
		List<WeakReference<IElementHandler>> handlerRefs,
		List<VirtualViewWeakReference> virtualViewRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new Payload(controlCase.Name, index, PayloadBytes);
		var instance = controlCase.Create(payload, index);
		var virtualView = instance.VirtualView;
		var handler = instance.Handler;

		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(virtualView);

		var nativeView = (AView)handler.PlatformView!;

		payloadRefs.Add(new PayloadWeakReference(controlCase.Name, new WeakReference<Payload>(payload), new WeakReference<byte[]>(payload.Bytes)));
		virtualViewRefs.Add(new VirtualViewWeakReference(controlCase.Name, new WeakReference<IView>(virtualView)));
		handlerRefs.Add(new WeakReference<IElementHandler>(handler));
		nativeViewRefs.Add(new WeakReference<AView>(nativeView));
		RetainedNativeViews.Add(nativeView);

		handler.DisconnectHandler();

		if (clearOwnerFields)
			ClearNativeOwnerReferences(nativeView);
	}

	static CaseInstance CreateLayoutCase(Payload payload, int index)
	{
		var view = new VerticalStackLayout
		{
			BindingContext = payload,
			Padding = new Thickness(index % 8)
		};

		return new CaseInstance(view, new LayoutHandler());
	}

	static CaseInstance CreateBorderCase(Payload payload, int index)
	{
		var view = new Border
		{
			BindingContext = payload,
			Padding = new Thickness(8)
		};

		return new CaseInstance(view, new BorderHandler());
	}

	static CaseInstance CreateScrollViewCase(Payload payload, int index)
	{
		var view = new ScrollView
		{
			BindingContext = payload
		};

		return new CaseInstance(view, new ScrollViewHandler());
	}

	static CaseInstance CreateRefreshViewCase(Payload payload, int index)
	{
		var view = new RefreshView
		{
			BindingContext = payload,
			Content = new Label { Text = $"Refresh payload {index}" }
		};

		return new CaseInstance(view, new RefreshViewHandler());
	}

	static CaseInstance CreateSwipeViewCase(Payload payload, int index)
	{
		var view = new SwipeView
		{
			BindingContext = payload
		};

		return new CaseInstance(view, new SwipeViewHandler());
	}

	static void ClearNativeOwnerReferences(AView nativeView)
	{
		SetPropertyIfPresent(nativeView, "CrossPlatformLayout", null);
		SetPropertyIfPresent(nativeView, "Clip", null);
		SetFieldIfPresent(nativeView, "_clip", null);
		SetFieldIfPresent(nativeView, "<Element>k__BackingField", null);
		SetFieldIfPresent(nativeView, "_content", null);
		SetFieldIfPresent(nativeView, "_contentView", null);

		var swipeItemsField = FindField(nativeView.GetType(), "_swipeItems");
		if (swipeItemsField?.GetValue(nativeView) is System.Collections.IDictionary swipeItems)
			swipeItems.Clear();
	}

	static void SetPropertyIfPresent(object target, string name, object? value)
	{
		var property = FindProperty(target.GetType(), name);
		if (property is not null && property.CanWrite)
			property.SetValue(target, value);
	}

	static void SetFieldIfPresent(object target, string name, object? value)
	{
		var field = FindField(target.GetType(), name);
		if (field is not null)
			field.SetValue(target, value);
	}

	static PropertyInfo? FindProperty(Type type, string name)
	{
		for (var current = type; current != null; current = current.BaseType)
		{
			var property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property != null)
				return property;
		}

		return null;
	}

	static FieldInfo? FindField(Type type, string name)
	{
		for (var current = type; current != null; current = current.BaseType)
		{
			var field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
				return field;
		}

		return null;
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

	sealed record ControlCase(string Name, Func<Payload, int, CaseInstance> Create);

	sealed record CaseInstance(IView VirtualView, IElementHandler Handler);

	sealed record VirtualViewWeakReference(string Control, WeakReference<IView> View);

	sealed record PayloadWeakReference(string Control, WeakReference<Payload> Payload, WeakReference<byte[]> Bytes);

	sealed class Payload
	{
		public Payload(string control, int id, int byteCount)
		{
			Control = control;
			Id = id;
			Bytes = new byte[byteCount];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)((id + control.Length + i) % 251);
			Bytes[^1] = (byte)((id + control.Length + Bytes.Length) % 251);
		}

		public string Control { get; }

		public int Id { get; }

		public byte[] Bytes { get; }
	}
}
