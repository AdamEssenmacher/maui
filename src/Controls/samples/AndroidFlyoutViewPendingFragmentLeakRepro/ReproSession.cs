using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Android.Views;
using AndroidX.DrawerLayout.Widget;
using AndroidX.Fragment.App;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using JavaObject = Java.Lang.Object;

namespace AndroidFlyoutViewPendingFragmentLeakRepro;

static class ReproSession
{
	const int CycleCount = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo PendingFragmentField =
		typeof(FlyoutViewHandler).GetField("_pendingFragment", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(FlyoutViewHandler), "_pendingFragment");

	static readonly MethodInfo UpdateDetailsFragmentViewMethod =
		typeof(FlyoutViewHandler).GetMethod("UpdateDetailsFragmentView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(nameof(FlyoutViewHandler), "UpdateDetailsFragmentView");

	public static ReproReport Run(IMauiContext mauiContext, FragmentManager fragmentManager)
	{
		ForceFullCollection();

		var control = RunScenario(
			mauiContext,
			fragmentManager,
			"control: force saved-state branch, explicitly dispose _pendingFragment, then disconnect",
			disposePendingFragmentBeforeDisconnect: true);

		var currentDisconnect = RunScenario(
			mauiContext,
			fragmentManager,
			"current disconnect: force saved-state branch, then disconnect without disposing _pendingFragment",
			disposePendingFragmentBeforeDisconnect: false);

		SetFragmentManagerSavedState(fragmentManager, false);

		return new ReproReport(control, currentDisconnect);
	}

	static ScenarioResult RunScenario(
		IMauiContext mauiContext,
		FragmentManager fragmentManager,
		string name,
		bool disposePendingFragmentBeforeDisconnect)
	{
		var handlerReferences = new List<WeakReference<PayloadFlyoutViewHandler>>();
		var payloadReferences = new List<WeakReference<LeakPayload>>();
		var pendingCallbackCount = 0;

		SetFragmentManagerSavedState(fragmentManager, true);

		for (var i = 0; i < CycleCount; i++)
		{
			if (CreateAndDisconnectHandler(
				mauiContext,
				fragmentManager,
				disposePendingFragmentBeforeDisconnect,
				handlerReferences,
				payloadReferences,
				i))
			{
				pendingCallbackCount++;
			}
		}

		ForceFullCollection();

		var result = new ScenarioResult(
			name,
			HandlerAlive: CountAlive(handlerReferences),
			PayloadAlive: CountAlive(payloadReferences),
			PendingCallbacksCreated: pendingCallbackCount);

		GC.KeepAlive(fragmentManager);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static bool CreateAndDisconnectHandler(
		IMauiContext mauiContext,
		FragmentManager fragmentManager,
		bool disposePendingFragmentBeforeDisconnect,
		List<WeakReference<PayloadFlyoutViewHandler>> handlerReferences,
		List<WeakReference<LeakPayload>> payloadReferences,
		int index)
	{
		var payload = new LeakPayload(index, PayloadBytes);
		var handler = new PayloadFlyoutViewHandler(payload);
		var flyoutPage = CreateFlyoutPage(index);

		SetFragmentManagerSavedState(fragmentManager, false);
		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(flyoutPage);

		SetFragmentManagerSavedState(fragmentManager, true);
		flyoutPage.Detail = CreateDetailPage(index, "replacement");
		UpdateDetailsFragmentViewMethod.Invoke(handler, null);

		var pendingFragment = (IDisposable?)PendingFragmentField.GetValue(handler);
		var pendingCallbackCreated = pendingFragment is not null;

		if (disposePendingFragmentBeforeDisconnect)
		{
			pendingFragment?.Dispose();
			PendingFragmentField.SetValue(handler, null);
		}

		handlerReferences.Add(new WeakReference<PayloadFlyoutViewHandler>(handler));
		payloadReferences.Add(new WeakReference<LeakPayload>(payload));

		((IElementHandler)handler).DisconnectHandler();

		return pendingCallbackCreated;
	}

	static FlyoutPage CreateFlyoutPage(int index)
	{
		return new FlyoutPage
		{
			Flyout = new ContentPage
			{
				Title = $"Flyout {index}",
				Content = new Label { Text = $"Flyout {index}" }
			},
			Detail = CreateDetailPage(index, "initial"),
			FlyoutLayoutBehavior = FlyoutLayoutBehavior.Popover
		};
	}

	static Page CreateDetailPage(int index, string name)
	{
		return new ContentPage
		{
			Title = $"Detail {name} {index}",
			Content = new Label { Text = $"Detail {name} {index}" }
		};
	}

	static void SetFragmentManagerSavedState(FragmentManager fragmentManager, bool value)
	{
		var javaObject = (JavaObject)fragmentManager;
		SetBooleanField(javaObject, "mStateSaved", value);
		SetBooleanField(javaObject, "mStopped", false);

		if (fragmentManager.IsStateSaved != value)
			throw new InvalidOperationException($"Could not set FragmentManager.IsStateSaved to {value}.");
	}

	static void SetBooleanField(JavaObject javaObject, string fieldName, bool value)
	{
		var type = javaObject.Class;

		while (type is not null)
		{
			try
			{
				var field = type.GetDeclaredField(fieldName);
				field.Accessible = true;
				field.SetBoolean(javaObject, value);
				return;
			}
			catch (Java.Lang.NoSuchFieldException)
			{
				type = type.Superclass;
			}
		}

		throw new MissingFieldException(javaObject.Class?.Name, fieldName);
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

	sealed class PayloadFlyoutViewHandler : FlyoutViewHandler
	{
		public PayloadFlyoutViewHandler(LeakPayload payload)
		{
			Payload = payload;
		}

		public LeakPayload Payload { get; }
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

	public sealed record ScenarioResult(
		string Name,
		int HandlerAlive,
		int PayloadAlive,
		int PendingCallbacksCreated)
	{
		public long PayloadBytesRetained => (long)PayloadAlive * PayloadBytes;
	}

	public sealed record ReproReport(ScenarioResult Control, ScenarioResult CurrentDisconnect)
	{
		public bool LeakProved =>
			Control.PendingCallbacksCreated == CycleCount &&
			CurrentDisconnect.PendingCallbacksCreated == CycleCount &&
			Control.HandlerAlive == 0 &&
			Control.PayloadAlive == 0 &&
			CurrentDisconnect.HandlerAlive >= CycleCount * 9 / 10 &&
			CurrentDisconnect.PayloadAlive >= CycleCount * 9 / 10;

		public string ToText()
		{
			var builder = new StringBuilder();
			builder.AppendLine("Android FlyoutViewHandler pending-fragment callback leak repro");
			builder.AppendLine($"Cycles: {CycleCount}");
			builder.AppendLine($"Payload per handler: {PayloadBytes / 1024 / 1024} MiB");
			builder.AppendLine($"Leak proved: {LeakProved}");
			builder.AppendLine();
			AppendScenario(builder, Control);
			builder.AppendLine();
			AppendScenario(builder, CurrentDisconnect);
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine(result.Name);
			builder.AppendLine($"  pending callbacks created: {result.PendingCallbacksCreated}/{CycleCount}");
			builder.AppendLine($"  disconnected handlers alive after full GC: {result.HandlerAlive}/{CycleCount}");
			builder.AppendLine($"  handler payloads alive after full GC: {result.PayloadAlive}/{CycleCount}");
			builder.AppendLine($"  retained payload bytes: {result.PayloadBytesRetained:N0}");
		}
	}
}
