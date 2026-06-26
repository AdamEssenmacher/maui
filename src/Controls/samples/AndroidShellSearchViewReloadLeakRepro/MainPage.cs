#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Android.Content;
using Android.OS;
using AndroidX.AppCompat.Widget;
using AndroidX.DrawerLayout.Widget;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Storage;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;
using AView = Android.Views.View;
using Environment = System.Environment;

namespace AndroidShellSearchViewReloadLeakRepro;

public class MainPage : ContentPage
{
	const int Iterations = 80;

	readonly Label _status;

	public MainPage()
	{
		_status = new Label
		{
			Text = "Running Android ShellSearchView reload leak repro...",
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

		RunOnMainThread(() =>
		{
			try
			{
				var context = Platform.CurrentActivity
					?? throw new InvalidOperationException("No current Android Activity.");

				result = RunScenarios(context);
			}
			catch (Exception ex)
			{
				exception = ex;
			}
		});

		var text = (exception is null
			? result!.ToString()
			: "RESULT: FAILED" + Environment.NewLine + exception) ?? string.Empty;

		_status.Text = text;

		var resultsPath = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
		File.WriteAllText(resultsPath, text);
		Android.Util.Log.Info("AndroidShellSearchViewReloadLeakRepro", text);

		await System.Threading.Tasks.Task.Delay(250);
		Process.KillProcess(Process.MyPid());
	}

	static ReproResult RunScenarios(Context context)
	{
		var control = RunControlScenario(context);
		var current = RunCurrentReloadScenario(context);

		return new ReproResult(control, current);
	}

	static ControlScenarioResult RunControlScenario(Context context)
	{
		var viewRefs = new List<WeakReference<TestShellSearchView>>();
		var retainedHandlers = CreateReplacingSearchViews(
			context,
			viewRefs,
			out var childTreesBeforeDispose,
			out var nativeViewsBeforeDispose);

		ForceGc();

		var result = new ControlScenarioResult(
			childTreesBeforeDispose,
			nativeViewsBeforeDispose,
			CountAlive(viewRefs),
			viewRefs.Count);

		GC.KeepAlive(retainedHandlers);
		return result;
	}

	static List<SearchHandler> CreateReplacingSearchViews(
		Context context,
		List<WeakReference<TestShellSearchView>> viewRefs,
		out int childTreesBeforeDispose,
		out int nativeViewsBeforeDispose)
	{
		var retainedHandlers = new List<SearchHandler>();
		TestShellSearchView? searchView = null;

		for (var i = 0; i < Iterations; i++)
		{
			if (searchView is not null)
			{
				((IShellSearchView)searchView).Dispose();
				viewRefs.Add(new WeakReference<TestShellSearchView>(searchView));
			}

			var shellContext = new FakeShellContext(context);
			searchView = new TestShellSearchView(context, shellContext);
			var searchHandler = CreateSearchHandler(i);
			retainedHandlers.Add(searchHandler);

			searchView.SearchHandler = searchHandler;
			((IShellSearchView)searchView).LoadView();
		}

		if (searchView is null)
			throw new InvalidOperationException("The control search view was not created.");

		childTreesBeforeDispose = searchView.ChildCount;
		nativeViewsBeforeDispose = CountNativeViews(searchView);

		((IShellSearchView)searchView).Dispose();
		viewRefs.Add(new WeakReference<TestShellSearchView>(searchView));

		return retainedHandlers;
	}

	static CurrentScenarioResult RunCurrentReloadScenario(Context context)
	{
		var viewRef = CreateReusedSearchViewAndDisposeAfterReloads(
			context,
			out var retainedHandlers,
			out var childTreesBeforeDispose,
			out var nativeViewsBeforeDispose);

		ForceGc();

		var searchViewAliveAfterDispose = viewRef.TryGetTarget(out _);

		var result = new CurrentScenarioResult(
			childTreesBeforeDispose,
			nativeViewsBeforeDispose,
			searchViewAliveAfterDispose);

		GC.KeepAlive(retainedHandlers);
		return result;
	}

	static WeakReference<TestShellSearchView> CreateReusedSearchViewAndDisposeAfterReloads(
		Context context,
		out List<SearchHandler> retainedHandlers,
		out int childTreesBeforeDispose,
		out int nativeViewsBeforeDispose)
	{
		var shellContext = new FakeShellContext(context);
		var searchView = new TestShellSearchView(context, shellContext);
		retainedHandlers = new List<SearchHandler>();

		for (var i = 0; i < Iterations; i++)
		{
			var searchHandler = CreateSearchHandler(i);
			retainedHandlers.Add(searchHandler);

			searchView.SearchHandler = searchHandler;
			((IShellSearchView)searchView).LoadView();
		}

		childTreesBeforeDispose = searchView.ChildCount;
		nativeViewsBeforeDispose = CountNativeViews(searchView);

		((IShellSearchView)searchView).Dispose();

		return new WeakReference<TestShellSearchView>(searchView);
	}

	static SearchHandler CreateSearchHandler(int index)
	{
		return new SearchHandler
		{
			Placeholder = $"Search customer records {index}",
			Query = $"account-{index:0000}",
			SearchBoxVisibility = SearchBoxVisibility.Expanded,
			ShowsResults = false
		};
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

	static int CountNativeViews(AView view)
	{
		var count = 1;

		if (view is Android.Views.ViewGroup viewGroup)
		{
			for (var i = 0; i < viewGroup.ChildCount; i++)
			{
				if (viewGroup.GetChildAt(i) is AView child)
					count += CountNativeViews(child);
			}
		}

		return count;
	}

	static void ForceGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(50);
		}
	}

	static void RunOnMainThread(Action action)
	{
		if (Looper.MyLooper() == Looper.MainLooper)
		{
			action();
			return;
		}

		using var completed = new ManualResetEventSlim();
		Exception? exception = null;

		new Handler(Looper.MainLooper!).Post(() =>
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				exception = ex;
			}
			finally
			{
				completed.Set();
			}
		});

		completed.Wait();

		if (exception is not null)
			throw exception;
	}

	sealed class TestShellSearchView : ShellSearchView
	{
		public TestShellSearchView(Context context, IShellContext shellContext)
			: base(context, shellContext)
		{
		}

		protected override SearchHandlerAppearanceTracker CreateSearchHandlerAppearanceTracker() => null!;
	}

	sealed class FakeShellContext : IShellContext
	{
		public FakeShellContext(Context context)
		{
			AndroidContext = context;
			Shell = new Shell();
		}

		public Context AndroidContext { get; }

		public DrawerLayout CurrentDrawerLayout => throw new NotSupportedException();

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

	readonly record struct ControlScenarioResult(
		int ChildTreesBeforeDispose,
		int NativeViewsBeforeDispose,
		int SearchViewsAliveAfterDispose,
		int SearchViewsCreated);

	readonly record struct CurrentScenarioResult(
		int ChildTreesBeforeDispose,
		int NativeViewsBeforeDispose,
		bool SearchViewAliveAfterDispose);

	readonly record struct ReproResult(ControlScenarioResult Control, CurrentScenarioResult Current)
	{
		public override string ToString()
		{
			var proven =
				Control.ChildTreesBeforeDispose == 1 &&
				Control.SearchViewsAliveAfterDispose == 0 &&
				Current.ChildTreesBeforeDispose == Iterations &&
				Current.NativeViewsBeforeDispose > Control.NativeViewsBeforeDispose &&
				Current.SearchViewAliveAfterDispose;

			var staleChildTrees = Current.ChildTreesBeforeDispose - Control.ChildTreesBeforeDispose;
			var extraNativeViews = Current.NativeViewsBeforeDispose - Control.NativeViewsBeforeDispose;

			var builder = new StringBuilder();
			builder.AppendLine(proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine($"control-replace-searchview-before-dispose: childTrees={Control.ChildTreesBeforeDispose}/1, nativeViews={Control.NativeViewsBeforeDispose}");
			builder.AppendLine($"control-after-dispose: searchViews={Control.SearchViewsAliveAfterDispose}/{Control.SearchViewsCreated}");
			builder.AppendLine($"leak-current-reused-searchview-before-dispose: childTrees={Current.ChildTreesBeforeDispose}/{Iterations}, nativeViews={Current.NativeViewsBeforeDispose}, staleChildTrees={staleChildTrees}, extraNativeViews={extraNativeViews}");
			builder.AppendLine($"leak-current-reused-searchview-after-dispose: searchViewAlive={(Current.SearchViewAliveAfterDispose ? 1 : 0)}/1");
			builder.AppendLine($"app-data-directory={FileSystem.AppDataDirectory}");
			builder.AppendLine($"dotnet-version={Environment.Version}");
			return builder.ToString();
		}
	}
}
