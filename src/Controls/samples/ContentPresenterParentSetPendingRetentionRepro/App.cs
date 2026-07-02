using System.Reflection;
using System.Text;
using Microsoft.Maui.Controls;

namespace ContentPresenterParentSetPendingRetentionRepro;

public class App : Application
{
	const int ContentCount = 120;
	const int PayloadBytes = 1024 * 1024;
	const string ResultPath = "/tmp/contentpresenter-parentset-pending-retention-results.txt";

	static readonly FieldInfo? ParentSetField = typeof(Element).GetField("ParentSet", BindingFlags.Instance | BindingFlags.NonPublic);

	public App()
	{
		Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), RunAndQuit);
	}

	protected override Window CreateWindow(IActivationState? activationState) =>
		new(new ContentPage
		{
			Content = new Label
			{
				Text = "Running ContentPresenter ParentSet pending retention repro...",
				Margin = new Thickness(24)
			}
		});

	void RunAndQuit()
	{
		try
		{
			File.WriteAllText(ResultPath, RunProof());
		}
		catch (Exception ex)
		{
			File.WriteAllText(ResultPath, "RESULT: ERROR" + Environment.NewLine + ex);
		}
		finally
		{
			Quit();
		}
	}

	static string RunProof()
	{
		var before = ForceFullGC();

		var control = CreateRun("control: parented presenter completes templated-parent waits", keepPresenterUnparented: false);
		var afterControlCreate = ForceFullGC();
		control.Measure();

		var current = CreateRun("current: retained off-tree presenter with pending ParentSet waits", keepPresenterUnparented: true);
		var afterCurrentCreate = ForceFullGC();
		current.Measure();

		var proven =
			control.PresenterAlive == 1 &&
			current.PresenterAlive == 1 &&
			control.PayloadsAlive == 0 &&
			control.PayloadBuffersAlive == 0 &&
			control.ContentViewsAlive == 0 &&
			control.PendingParentSetHandlers == 0 &&
			current.PayloadsAlive == ContentCount &&
			current.PayloadBuffersAlive == ContentCount &&
			current.ContentViewsAlive == ContentCount &&
			current.PendingParentSetHandlers >= ContentCount;

		var builder = new StringBuilder();
		builder.AppendLine(proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
		builder.AppendLine("ContentPresenterParentSetPendingRetentionRepro");
		builder.AppendLine("Retained ContentPresenter instances in both scenarios: 1");
		builder.AppendLine($"Content replacements before parenting: {ContentCount}");
		builder.AppendLine($"Payload per removed content view: {PayloadBytes / 1024 / 1024:0.0} MiB");
		builder.AppendLine();
		AppendRun(builder, control, before, afterControlCreate);
		builder.AppendLine();
		AppendRun(builder, current, afterControlCreate, afterCurrentCreate);
		builder.AppendLine();
		builder.AppendLine("Leak path: retained off-tree ContentPresenter -> ParentSet event invocation list -> TemplateUtilities.GetRealParentAsync handler -> TaskCompletionSource continuations -> async ContentPresenter.OnContentChanged state machine -> removed content view -> payload.");
		builder.AppendLine("The control keeps the presenter parented, so FindTemplatedParentAsync completes instead of storing a pending ParentSet handler for each removed content view.");
		builder.AppendLine("The current path keeps the presenter live but off-tree, repeatedly assigns and clears Content, and leaves each removed content view captured by an incomplete templated-parent wait.");
		builder.AppendLine("One extra pending ParentSet handler is expected from ContentPresenter's constructor binding to RelativeBindingSource.TemplatedParent.");
		builder.AppendLine($"dotnet-version: {Environment.Version}");

		GC.KeepAlive(control.RetainedPresenter);
		GC.KeepAlive(control.RetainedHost);
		GC.KeepAlive(current.RetainedPresenter);

		return builder.ToString();
	}

	static RunResult CreateRun(string name, bool keepPresenterUnparented)
	{
		var result = new RunResult(name);
		var presenter = new ContentPresenter();
		result.RetainedPresenter = presenter;
		result.PresenterRef = new WeakReference<ContentPresenter>(presenter);

		if (!keepPresenterUnparented)
		{
			var host = new ContentView();
			presenter.Parent = host;
			host.Parent = Current;
			result.RetainedHost = host;
		}

		for (var i = 0; i < ContentCount; i++)
		{
			var payload = new PayloadCarrier(i, PayloadBytes);
			var content = new PayloadContentView(payload);

			presenter.Content = content;
			presenter.Content = null;

			result.PayloadRefs.Add(new WeakReference<PayloadCarrier>(payload));
			result.PayloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
			result.ContentViewRefs.Add(new WeakReference<PayloadContentView>(content));
		}

		return result;
	}

	static void AppendRun(StringBuilder builder, RunResult result, long beforeBytes, long afterBytes)
	{
		builder.AppendLine($"Run: {result.Name}");
		builder.AppendLine("  retained presenters: 1");
		builder.AppendLine($"  presenter alive after full GC: {result.PresenterAlive}/1");
		builder.AppendLine($"  pending ParentSet handlers on presenter: {result.PendingParentSetHandlers}");
		builder.AppendLine($"  removed content views alive after full GC: {result.ContentViewsAlive}/{ContentCount}");
		builder.AppendLine($"  payload objects alive after full GC: {result.PayloadsAlive}/{ContentCount}");
		builder.AppendLine($"  payload buffers alive after full GC: {result.PayloadBuffersAlive}/{ContentCount}");
		builder.AppendLine($"  retained payload bytes: {result.RetainedPayloadBytes / 1024d / 1024d:0.0} MiB");
		builder.AppendLine($"  managed heap delta: {(afterBytes - beforeBytes) / 1024d / 1024d:0.0} MiB");
	}

	static long ForceFullGC()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
			GC.WaitForPendingFinalizers();
		}

		return GC.GetTotalMemory(forceFullCollection: true);
	}

	static int CountPendingParentSetHandlers(ContentPresenter presenter)
	{
		if (ParentSetField?.GetValue(presenter) is not MulticastDelegate parentSet)
			return 0;

		return parentSet.GetInvocationList().Length;
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

	sealed class RunResult
	{
		public RunResult(string name)
		{
			Name = name;
		}

		public string Name { get; }
		public ContentPresenter? RetainedPresenter { get; set; }
		public ContentView? RetainedHost { get; set; }
		public WeakReference<ContentPresenter>? PresenterRef { get; set; }
		public List<WeakReference<PayloadCarrier>> PayloadRefs { get; } = new();
		public List<WeakReference<byte[]>> PayloadBufferRefs { get; } = new();
		public List<WeakReference<PayloadContentView>> ContentViewRefs { get; } = new();
		public int PresenterAlive { get; private set; }
		public int PayloadsAlive { get; private set; }
		public int PayloadBuffersAlive { get; private set; }
		public int ContentViewsAlive { get; private set; }
		public int PendingParentSetHandlers { get; private set; }
		public long RetainedPayloadBytes => PayloadBuffersAlive * (long)PayloadBytes;

		public void Measure()
		{
			PresenterAlive = PresenterRef?.TryGetTarget(out _) == true ? 1 : 0;
			PayloadsAlive = CountAlive(PayloadRefs);
			PayloadBuffersAlive = CountAlive(PayloadBufferRefs);
			ContentViewsAlive = CountAlive(ContentViewRefs);
			PendingParentSetHandlers = RetainedPresenter is not null ? CountPendingParentSetHandlers(RetainedPresenter) : 0;
		}
	}

	sealed class PayloadContentView : ContentView
	{
		readonly PayloadCarrier _payload;

		public PayloadContentView(PayloadCarrier payload)
		{
			_payload = payload;
			Content = new Label { Text = "Removed payload " + payload.Id };
		}
	}

	sealed class PayloadCarrier
	{
		public PayloadCarrier(int id, int byteCount)
		{
			Id = id;
			Buffer = new byte[byteCount];
			Buffer[0] = (byte)(id % 255);
		}

		public int Id { get; }
		public byte[] Buffer { get; }
	}
}
