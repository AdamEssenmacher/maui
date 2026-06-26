#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Android.Content;
using Android.Widget;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Storage;
using AMenu = Android.Views.IMenu;
using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;

namespace AndroidListViewActionModeSubscriptionLeakRepro;

public class MainPage : ContentPage
{
	const int Iterations = 80;
	const int RebuildsPerAdapter = 8;

	static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
	static readonly PropertyInfo ActionModeContextProperty = typeof(CellAdapter).GetProperty("ActionModeContext", InstanceNonPublic)
		?? throw new MissingMemberException(nameof(CellAdapter), "ActionModeContext");
	static readonly MethodInfo CreateContextMenuMethod = typeof(CellAdapter).GetMethod("CreateContextMenu", InstanceNonPublic)
		?? throw new MissingMethodException(nameof(CellAdapter), "CreateContextMenu");
	static readonly MethodInfo OnPrepareActionModeImplMethod = typeof(CellAdapter).GetMethod("OnPrepareActionModeImpl", InstanceNonPublic)
		?? throw new MissingMethodException(nameof(CellAdapter), "OnPrepareActionModeImpl");
	static readonly MethodInfo OnDestroyActionModeImplMethod = typeof(CellAdapter).GetMethod("OnDestroyActionModeImpl", InstanceNonPublic)
		?? throw new MissingMethodException(nameof(CellAdapter), "OnDestroyActionModeImpl");

	readonly Label _status;

	public MainPage()
	{
		_status = new Label
		{
			Text = "Running Android ListView ActionMode subscription leak repro...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		ReproResult? result = null;
		Exception? exception = null;

		try
		{
			result = await RunScenariosAsync();
		}
		catch (Exception ex)
		{
			exception = ex;
		}

		var text = exception is null
			? (result ?? throw new InvalidOperationException("Repro completed without a result.")).ToString()
			: "RESULT: FAILED" + Environment.NewLine + exception;

		_status.Text = text;
		WriteResult(text);

		await Task.Delay(250);
		Process.GetCurrentProcess().Kill();
	}

	static async Task<ReproResult> RunScenariosAsync()
	{
		await Task.Yield();

		var context = GetAndroidContext();
		var control = RunScenario(context, rebuildActiveMenu: false);
		var current = RunScenario(context, rebuildActiveMenu: true);

		return new ReproResult(control, current);
	}

	static ScenarioResult RunScenario(Context context, bool rebuildActiveMenu)
	{
		LeakProbeRegistry.Reset();

		var retainedSources = new List<RetainedActionSource>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			var source = new RetainedActionSource(i);
			retainedSources.Add(source);

			DriveAdapter(context, source, i, rebuildActiveMenu);
		}

		ForceGc();

		var result = new ScenarioResult(
			CountAlive(LeakProbeRegistry.AdapterReferences),
			LeakProbeRegistry.AdapterReferences.Count,
			CountAlive(LeakProbeRegistry.PayloadReferences),
			LeakProbeRegistry.PayloadReferences.Count);

		GC.KeepAlive(retainedSources);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void DriveAdapter(Context context, RetainedActionSource source, int index, bool rebuildActiveMenu)
	{
		var adapter = new PayloadCellAdapter(context, source.Cell, index);

		using var anchor = new AView(context);
		using var popup = new PopupMenu(context, anchor);
		var menu = popup.Menu ?? throw new InvalidOperationException("Popup menu was not created.");

		SetActionModeContext(adapter, source.Cell);
		CreateContextMenu(adapter, menu);

		if (rebuildActiveMenu)
		{
			for (var rebuild = 0; rebuild < RebuildsPerAdapter; rebuild++)
			{
				source.Action.Text = $"Archive {index}:{rebuild}";
				source.Command.RaiseCanExecuteChanged();
				PrepareContextMenu(adapter, menu);
			}
		}

		DestroyActionMode(adapter);
		adapter.Dispose();
	}

	static Context GetAndroidContext()
	{
		return Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
			?? Android.App.Application.Context
			?? throw new InvalidOperationException("No Android context is available.");
	}

	static void SetActionModeContext(CellAdapter adapter, Cell cell)
	{
		ActionModeContextProperty.SetValue(adapter, cell);
	}

	static void CreateContextMenu(CellAdapter adapter, AMenu menu)
	{
		CreateContextMenuMethod.Invoke(adapter, new object[] { menu });
	}

	static void PrepareContextMenu(CellAdapter adapter, AMenu menu)
	{
		OnPrepareActionModeImplMethod.Invoke(adapter, new object[] { menu });
	}

	static void DestroyActionMode(CellAdapter adapter)
	{
		OnDestroyActionModeImplMethod.Invoke(adapter, Array.Empty<object>());
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

	static void ForceGc()
	{
		for (var i = 0; i < 6; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			Thread.Sleep(75);
		}
	}

	static void WriteResult(string text)
	{
		var path = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
		File.WriteAllText(path, text);
	}

	readonly record struct ScenarioResult(
		int AdaptersAlive,
		int AdaptersCreated,
		int PayloadsAlive,
		int PayloadsCreated);

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult Current)
	{
		public override string ToString()
		{
			var proven =
				Control.AdaptersAlive == 0 &&
				Control.PayloadsAlive == 0 &&
				Current.AdaptersAlive == Iterations &&
				Current.PayloadsAlive == Iterations;

			var leakedBytes = Current.PayloadsAlive * PayloadCellAdapter.PayloadBytes;
			var builder = new StringBuilder();
			builder.AppendLine(proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine($"control-single-create-destroy: adapters={Control.AdaptersAlive}/{Control.AdaptersCreated}, payloads={Control.PayloadsAlive}/{Control.PayloadsCreated}");
			builder.AppendLine($"leak-rebuild-before-destroy: adapters={Current.AdaptersAlive}/{Current.AdaptersCreated}, payloads={Current.PayloadsAlive}/{Current.PayloadsCreated}, retainedBytes={leakedBytes}, retainedMiB={leakedBytes / 1024d / 1024d:0.0}");
			builder.AppendLine($"iterations={Iterations}");
			builder.AppendLine($"rebuildsPerAdapter={RebuildsPerAdapter}");
			builder.AppendLine($"payloadBytesPerAdapter={PayloadCellAdapter.PayloadBytes}");
			builder.AppendLine($"dotnet-version={Environment.Version}");
			return builder.ToString();
		}
	}
}

sealed class RetainedActionSource
{
	public RetainedActionSource(int index)
	{
#pragma warning disable CS0618
		Cell = new TextCell
#pragma warning restore CS0618
		{
			Text = $"Customer {index}",
			Detail = "Swipe or long-press actions for a retained ListView row"
		};

		Command = new TrackingCommand();
		Action = new MenuItem
		{
			Text = "Archive",
			Command = Command
		};

		Cell.ContextActions.Add(Action);
	}

#pragma warning disable CS0618
	public Cell Cell { get; }
#pragma warning restore CS0618

	public MenuItem Action { get; }

	public TrackingCommand Command { get; }
}

sealed class TrackingCommand : ICommand
{
	public event EventHandler? CanExecuteChanged;

	public bool CanExecute(object? parameter) => true;

	public void Execute(object? parameter)
	{
	}

	public void RaiseCanExecuteChanged()
	{
		CanExecuteChanged?.Invoke(this, EventArgs.Empty);
	}
}

sealed class PayloadCellAdapter : CellAdapter
{
	public const int PayloadBytes = 1024 * 1024;

	readonly Context _context;
#pragma warning disable CS0618
	readonly Cell _cell;
#pragma warning restore CS0618
	readonly byte[] _payload;

#pragma warning disable CS0618
	public PayloadCellAdapter(Context context, Cell cell, int index)
#pragma warning restore CS0618
		: base(context)
	{
		_context = context;
		_cell = cell;
		_payload = new byte[PayloadBytes];
		_payload[0] = (byte)index;

		LeakProbeRegistry.AdapterReferences.Add(new WeakReference<PayloadCellAdapter>(this));
		LeakProbeRegistry.PayloadReferences.Add(new WeakReference<byte[]>(_payload));
	}

	public override int Count => 1;

	public override object this[int position] => _cell.BindingContext ?? _cell;

	public override long GetItemId(int position) => position;

	public override AView GetView(int position, AView? convertView, AViewGroup? parent)
	{
		return convertView ?? new AView(_context);
	}

#pragma warning disable CS0618
	protected override Cell GetCellForPosition(int position)
#pragma warning restore CS0618
	{
		return _cell;
	}
}

static class LeakProbeRegistry
{
	public static List<WeakReference<PayloadCellAdapter>> AdapterReferences { get; } = new();

	public static List<WeakReference<byte[]>> PayloadReferences { get; } = new();

	public static void Reset()
	{
		AdapterReferences.Clear();
		PayloadReferences.Clear();
	}
}
