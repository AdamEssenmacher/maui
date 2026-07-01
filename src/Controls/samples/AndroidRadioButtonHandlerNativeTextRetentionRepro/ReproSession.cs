#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Android.Runtime;
using AndroidX.AppCompat.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using MauiRadioButton = Microsoft.Maui.Controls.RadioButton;

namespace AndroidRadioButtonHandlerNativeTextRetentionRepro;

public static class ReproSession
{
	const int Iterations = 1024;
	const int PayloadChars = 16 * 1024;
	const long PayloadBytes = PayloadChars * 2L;

	public static async Task<string> RunAsync(Page hostPage)
	{
		var mauiContext = await WaitForMauiContextAsync(hostPage);

		var control = await RunScenarioAsync("explicit native text clear", mauiContext, clearNativeTextBeforeDisconnect: true);
		await ForceCollectionsAsync();

		var current = await RunScenarioAsync("current MAUI disconnect", mauiContext, clearNativeTextBeforeDisconnect: false);
		await ForceCollectionsAsync();

		var controlResult = Inspect(control);
		var currentResult = Inspect(current);

		var report = $"""
			Android RadioButtonHandler native text retention repro
			Iterations: {Iterations}
			Per-radio generated content: {PayloadChars:N0} chars ~= {FormatBytes(PayloadBytes)} UTF-16 text
			Expected retained native text if every slot survives: {FormatBytes(PayloadBytes * Iterations)}

			Control ({controlResult.Name})
			  Native AppCompatRadioButtons retained by JNI global refs: {controlResult.NativeRadioButtonsRetained}/{Iterations}
			  Assigned native text slots: {controlResult.AssignedTextSlots}/{Iterations}
			  Payload-sized native text slots: {controlResult.PayloadSizedTextSlots}/{Iterations}
			  Retained native text payload: {FormatBytes(controlResult.RetainedTextBytes)}
			  Managed RadioButton wrappers alive: {controlResult.ManagedRadioButtonsAlive}/{Iterations}
			  Managed RadioButtonHandler wrappers alive: {controlResult.ManagedHandlersAlive}/{Iterations}

			Current MAUI ({currentResult.Name})
			  Native AppCompatRadioButtons retained by JNI global refs: {currentResult.NativeRadioButtonsRetained}/{Iterations}
			  Assigned native text slots: {currentResult.AssignedTextSlots}/{Iterations}
			  Payload-sized native text slots: {currentResult.PayloadSizedTextSlots}/{Iterations}
			  Retained native text payload: {FormatBytes(currentResult.RetainedTextBytes)}
			  Managed RadioButton wrappers alive: {currentResult.ManagedRadioButtonsAlive}/{Iterations}
			  Managed RadioButtonHandler wrappers alive: {currentResult.ManagedHandlersAlive}/{Iterations}

			Verdict: {(currentResult.RetainedTextBytes > controlResult.RetainedTextBytes ? "PROVED" : "NOT PROVED")}
			""";

		control.Dispose();
		current.Dispose();

		return report;
	}

	static async Task<IMauiContext> WaitForMauiContextAsync(Page hostPage)
	{
		for (var i = 0; i < 50; i++)
		{
			if (hostPage.Handler?.MauiContext is IMauiContext mauiContext)
				return mauiContext;

			await Task.Delay(100);
		}

		throw new InvalidOperationException("The host page did not receive a MAUI context.");
	}

	static async Task<ScenarioSnapshot> RunScenarioAsync(string name, IMauiContext mauiContext, bool clearNativeTextBeforeDisconnect)
	{
		var samples = new List<IterationSnapshot>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			samples.Add(CreateIteration(i, mauiContext, clearNativeTextBeforeDisconnect));

			if ((i + 1) % 128 == 0)
				await ForceCollectionsAsync();
		}

		return new ScenarioSnapshot(name, samples);
	}

	static IterationSnapshot CreateIteration(int index, IMauiContext mauiContext, bool clearNativeTextBeforeDisconnect)
	{
		var radioButton = new MauiRadioButton
		{
			Content = CreatePayload(index)
		};

		var handler = new RadioButtonHandler();
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(radioButton);

		var platformRadioButton = (AppCompatRadioButton)handler.PlatformView;
		RadioButtonHandler.MapContent(handler, radioButton);

		if (platformRadioButton.Text?.Length < PayloadChars)
			throw new InvalidOperationException($"Native radio text was not assigned for iteration {index}.");

		var nativeRoot = new NativePeerRoot(platformRadioButton);
		var handlerWeak = new WeakReference(handler);
		var radioButtonWeak = new WeakReference(radioButton);

		if (clearNativeTextBeforeDisconnect)
			platformRadioButton.Text = string.Empty;

		((IElementHandler)handler).DisconnectHandler();
		radioButton.Content = null;

		handler = null!;
		radioButton = null!;
		platformRadioButton = null!;

		return new IterationSnapshot(nativeRoot, handlerWeak, radioButtonWeak);
	}

	static ScenarioResult Inspect(ScenarioSnapshot scenario)
	{
		var nativeRadioButtons = 0;
		var assignedTextSlots = 0;
		var payloadSizedTextSlots = 0;
		var retainedTextBytes = 0L;
		var managedHandlersAlive = 0;
		var managedRadioButtonsAlive = 0;

		foreach (var sample in scenario.Samples)
		{
			if (sample.HandlerWeak.IsAlive)
				managedHandlersAlive++;
			if (sample.RadioButtonWeak.IsAlive)
				managedRadioButtonsAlive++;

			var radioButton = sample.NativeRoot.Get<AppCompatRadioButton>();
			if (radioButton == null)
				continue;

			nativeRadioButtons++;

			var text = radioButton.Text;
			if (string.IsNullOrEmpty(text))
				continue;

			assignedTextSlots++;

			if (text.Length >= PayloadChars)
			{
				payloadSizedTextSlots++;
				retainedTextBytes += text.Length * 2L;
			}
		}

		return new ScenarioResult(
			scenario.Name,
			nativeRadioButtons,
			assignedTextSlots,
			payloadSizedTextSlots,
			retainedTextBytes,
			managedHandlersAlive,
			managedRadioButtonsAlive);
	}

	static string CreatePayload(int index)
	{
		var prefix = $"RadioButton option {index:D4}: ";
		var builder = new StringBuilder(PayloadChars);
		builder.Append(prefix);

		var payloadChar = (char)('A' + (index % 26));
		while (builder.Length < PayloadChars)
			builder.Append(payloadChar);

		return builder.ToString();
	}

	static async Task ForceCollectionsAsync()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			await Task.Delay(100);
		}
	}

	static string FormatBytes(long bytes)
	{
		const double KiB = 1024;
		const double MiB = 1024 * 1024;
		if (bytes < MiB)
			return $"{bytes / KiB:0.0} KiB";

		return $"{bytes / MiB:0.0} MiB";
	}

	sealed record ScenarioSnapshot(string Name, List<IterationSnapshot> Samples) : IDisposable
	{
		public void Dispose()
		{
			foreach (var sample in Samples)
				sample.NativeRoot.Dispose();
		}
	}

	sealed record IterationSnapshot(NativePeerRoot NativeRoot, WeakReference HandlerWeak, WeakReference RadioButtonWeak);

	sealed record ScenarioResult(
		string Name,
		int NativeRadioButtonsRetained,
		int AssignedTextSlots,
		int PayloadSizedTextSlots,
		long RetainedTextBytes,
		int ManagedHandlersAlive,
		int ManagedRadioButtonsAlive);

	sealed class NativePeerRoot : IDisposable
	{
		IntPtr _handle;

		public NativePeerRoot(Java.Lang.Object peer)
		{
			_handle = JNIEnv.NewGlobalRef(peer.Handle);
		}

		public T? Get<T>() where T : Java.Lang.Object
		{
			if (_handle == IntPtr.Zero)
				return null;

			return Java.Lang.Object.GetObject<T>(_handle, JniHandleOwnership.DoNotTransfer);
		}

		public void Dispose()
		{
			if (_handle == IntPtr.Zero)
				return;

			JNIEnv.DeleteGlobalRef(_handle);
			_handle = IntPtr.Zero;
		}
	}
}
