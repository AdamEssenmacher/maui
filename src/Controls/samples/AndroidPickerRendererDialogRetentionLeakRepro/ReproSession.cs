#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Android.App;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.Android;
using Microsoft.Maui.Controls.Compatibility.Platform.Android.AppCompat;

namespace AndroidPickerRendererDialogRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRenderers,
	int AlivePickers,
	int AliveDialogs,
	int AlivePayloads,
	int AlivePayloadByteArrays,
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
		Control.AlivePickers == 0 &&
		Control.AliveDialogs == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AlivePickers == Attempts &&
		Current.AliveDialogs == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadByteArrays == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidPickerRendererDialogRetentionLeakRepro",
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
			$"  disposed native renderers created: {stats.Attempts}",
			$"  renderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}",
			$"  Pickers alive after full GC: {stats.AlivePickers}/{stats.Attempts}",
			$"  AlertDialogs alive after full GC: {stats.AliveDialogs}/{stats.Attempts}",
			$"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
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
	const int Attempts = 20;
	const int PayloadBytes = 4 * 1024 * 1024;

	static readonly FieldInfo DialogField =
		typeof(PickerRenderer).BaseType?.GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(PickerRenderer), "_dialog");

	static readonly FieldInfo ElementHandlerField =
		typeof(Element).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(Element), "_handler");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: dismiss dialog before disposing PickerRenderer",
			dismissDialogBeforeDispose: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: dispose PickerRenderer with open dialog",
			dismissDialogBeforeDispose: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool dismissDialogBeforeDispose)
	{
		var rendererRefs = new List<WeakReference<PickerRenderer>>(Attempts);
		var pickerRefs = new List<WeakReference<Picker>>(Attempts);
		var dialogRefs = new List<WeakReference<AlertDialog>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedRendererWithDialog(
				mauiContext,
				dismissDialogBeforeDispose,
				rendererRefs,
				pickerRefs,
				dialogRefs,
				payloadRefs,
				i);

			if (i % 5 == 0)
				await Task.Yield();
		}

		await Task.Delay(750);
		ForceFullGc();

		var aliveRenderers = rendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePickers = pickerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveDialogs = dialogRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.Payload.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			aliveRenderers,
			alivePickers,
			aliveDialogs,
			alivePayloads,
			alivePayloadByteArrays,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisposedRendererWithDialog(
		IMauiContext mauiContext,
		bool dismissDialogBeforeDispose,
		List<WeakReference<PickerRenderer>> rendererRefs,
		List<WeakReference<Picker>> pickerRefs,
		List<WeakReference<AlertDialog>> dialogRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var picker = new Picker
		{
			Title = $"Choose account {index}",
			BindingContext = payload
		};

		for (var item = 0; item < 24; item++)
			picker.Items.Add($"Warehouse {index:00}-{item:00}");

		var renderer = new PickerRenderer(mauiContext.Context ?? throw new InvalidOperationException("Android context is not available."));
		var contextHandler = new ContextOnlyHandler(mauiContext, picker);

		payloadRefs.Add(new PayloadWeakReference(new WeakReference<Payload>(payload), new WeakReference<byte[]>(payload.Bytes)));
		pickerRefs.Add(new WeakReference<Picker>(picker));
		rendererRefs.Add(new WeakReference<PickerRenderer>(renderer));

		ElementHandlerField.SetValue(picker, contextHandler);
		((IVisualElementRenderer)renderer).SetElement(picker);
		ElementHandlerField.SetValue(picker, null);
		contextHandler.DisconnectHandler();

		((IPickerRenderer)renderer).OnClick();

		if (DialogField.GetValue(renderer) is not AlertDialog dialog)
			throw new InvalidOperationException("PickerRenderer did not create an AlertDialog.");

		dialogRefs.Add(new WeakReference<AlertDialog>(dialog));

		if (dismissDialogBeforeDispose)
			DismissAndClearDialog(renderer, dialog);

		renderer.Dispose();
	}

	static void DismissAndClearDialog(PickerRenderer renderer, AlertDialog dialog)
	{
		if (dialog.IsShowing)
			dialog.Dismiss();

		dialog.Dispose();
		DialogField.SetValue(renderer, null);
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

	sealed record PayloadWeakReference(WeakReference<Payload> Payload, WeakReference<byte[]> Bytes);

	sealed class ContextOnlyHandler : IViewHandler
	{
		public ContextOnlyHandler(IMauiContext mauiContext, IView virtualView)
		{
			MauiContext = mauiContext;
			VirtualView = virtualView;
		}

		public bool HasContainer { get; set; }

		public object? ContainerView => null;

		public object? PlatformView => null;

		public IView? VirtualView { get; private set; }

		IElement? IElementHandler.VirtualView => VirtualView;

		public IMauiContext? MauiContext { get; private set; }

		public void DisconnectHandler()
		{
			VirtualView = null;
			MauiContext = null;
		}

		public void Invoke(string command, object? args = null)
		{
		}

		public void SetMauiContext(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public void SetVirtualView(IElement view)
		{
			VirtualView = (IView)view;
		}

		public void UpdateValue(string property)
		{
		}

		public Microsoft.Maui.Graphics.Size GetDesiredSize(double widthConstraint, double heightConstraint)
		{
			return Microsoft.Maui.Graphics.Size.Zero;
		}

		public void PlatformArrange(Microsoft.Maui.Graphics.Rect frame)
		{
		}
	}

	sealed class Payload
	{
		public Payload(int id, int byteCount)
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
}
