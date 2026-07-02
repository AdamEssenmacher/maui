using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices.Sensors;

namespace GeolocationStaticEventRetentionRepro;

static class ReproSession
{
	const int CycleCount = 80;
	const int PayloadBytes = 1024 * 1024;

	public static ReproReport Run()
	{
		ForceFullCollection();

		var control = RunScenario(
			"control: transient pages never subscribe to Geolocation static events",
			ScenarioMode.NoSubscription);

		var mitigation = RunScenario(
			"mitigation: transient pages unsubscribe with -= before cleanup",
			ScenarioMode.Unsubscribe);

		var current = RunScenario(
			"current cleanup: transient pages call StopListeningForeground() but do not unsubscribe",
			ScenarioMode.StopOnly);

		return new ReproReport(control, mitigation, current);
	}

	static ScenarioResult RunScenario(string name, ScenarioMode mode)
	{
		var pageReferences = new List<WeakReference<PayloadPage>>(CycleCount);
		var viewModelReferences = new List<WeakReference<PayloadViewModel>>(CycleCount);
		var payloadReferences = new List<WeakReference<LeakPayload>>(CycleCount);

		for (var i = 0; i < CycleCount; i++)
			CreateCycle(i, mode, pageReferences, viewModelReferences, payloadReferences);

		ForceFullCollection();

		return new ScenarioResult(
			name,
			AlivePages: CountAlive(pageReferences),
			AliveViewModels: CountAlive(viewModelReferences),
			AlivePayloads: CountAlive(payloadReferences));
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateCycle(
		int index,
		ScenarioMode mode,
		List<WeakReference<PayloadPage>> pageReferences,
		List<WeakReference<PayloadViewModel>> viewModelReferences,
		List<WeakReference<LeakPayload>> payloadReferences)
	{
		var page = new PayloadPage(index, PayloadBytes);

		pageReferences.Add(new WeakReference<PayloadPage>(page));
		viewModelReferences.Add(new WeakReference<PayloadViewModel>(page.ViewModel));
		payloadReferences.Add(new WeakReference<LeakPayload>(page.ViewModel.Payload));

		if (mode != ScenarioMode.NoSubscription)
			page.Subscribe();

		if (mode == ScenarioMode.Unsubscribe)
			page.UnsubscribeAndStop();
		else
			page.StopOnly();
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
			Thread.Sleep(50);
		}
	}

	enum ScenarioMode
	{
		NoSubscription,
		StopOnly,
		Unsubscribe
	}

	sealed class PayloadPage : ContentPage
	{
		public PayloadPage(int id, int payloadBytes)
		{
			ViewModel = new PayloadViewModel(id, payloadBytes);
			BindingContext = ViewModel;
			Content = new Label { Text = $"Geolocation payload page {id}" };
		}

		public PayloadViewModel ViewModel { get; }

		public void Subscribe()
		{
			Geolocation.LocationChanged += OnLocationChanged;
			Geolocation.ListeningFailed += OnListeningFailed;
		}

		public void StopOnly()
		{
			SafeStopListening();
		}

		public void UnsubscribeAndStop()
		{
			Geolocation.LocationChanged -= OnLocationChanged;
			Geolocation.ListeningFailed -= OnListeningFailed;
			SafeStopListening();
		}

		void OnLocationChanged(object? sender, GeolocationLocationChangedEventArgs e)
		{
			ViewModel.Touch(e.Location);
		}

		void OnListeningFailed(object? sender, GeolocationListeningFailedEventArgs e)
		{
			ViewModel.Touch(e.Error);
		}

		static void SafeStopListening()
		{
			try
			{
				Geolocation.StopListeningForeground();
			}
			catch (Exception ex) when (
				ex is FeatureNotSupportedException ||
				ex is FeatureNotEnabledException ||
				ex is PermissionException ||
				ex is UnauthorizedAccessException)
			{
			}
		}
	}

	sealed class PayloadViewModel
	{
		int _touchCount;

		public PayloadViewModel(int id, int payloadBytes)
		{
			Id = id;
			Payload = new LeakPayload(id, payloadBytes);
		}

		public int Id { get; }

		public LeakPayload Payload { get; }

		public void Touch(object? value)
		{
			if (value is not null)
				_touchCount++;
		}
	}

	sealed class LeakPayload
	{
		public LeakPayload(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)((id + i) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}

	public sealed record ScenarioResult(
		string Name,
		int AlivePages,
		int AliveViewModels,
		int AlivePayloads)
	{
		public long RetainedPayloadBytes => (long)AlivePayloads * PayloadBytes;
	}

	public sealed record ReproReport(
		ScenarioResult Control,
		ScenarioResult Mitigation,
		ScenarioResult Current)
	{
		public bool LeakProved =>
			Control.AlivePages == 0 &&
			Control.AliveViewModels == 0 &&
			Control.AlivePayloads == 0 &&
			Mitigation.AlivePages == 0 &&
			Mitigation.AliveViewModels == 0 &&
			Mitigation.AlivePayloads == 0 &&
			Current.AlivePages >= CycleCount * 9 / 10 &&
			Current.AliveViewModels >= CycleCount * 9 / 10 &&
			Current.AlivePayloads >= CycleCount * 9 / 10;

		public string ToText()
		{
			var builder = new StringBuilder();
			builder.AppendLine("Geolocation static event subscriber retention repro");
			builder.AppendLine($"Cycles: {CycleCount}");
			builder.AppendLine($"Payload per page/view-model: {PayloadBytes / 1024 / 1024} MiB");
			builder.AppendLine("Root under test: Geolocation.Default singleton LocationChanged/ListeningFailed multicast delegates");
			builder.AppendLine("Cleanup under test: StopListeningForeground() without matching -= event cleanup");
			builder.AppendLine($"Leak proved: {LeakProved}");
			builder.AppendLine($"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
			builder.AppendLine();
			AppendScenario(builder, Control);
			builder.AppendLine();
			AppendScenario(builder, Mitigation);
			builder.AppendLine();
			AppendScenario(builder, Current);
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine(result.Name);
			builder.AppendLine($"  retained pages after full GC: {result.AlivePages}/{CycleCount}");
			builder.AppendLine($"  retained view-models after full GC: {result.AliveViewModels}/{CycleCount}");
			builder.AppendLine($"  retained payloads after full GC: {result.AlivePayloads}/{CycleCount}");
			builder.AppendLine($"  retained payload bytes: {result.RetainedPayloadBytes:N0}");
		}
	}
}
