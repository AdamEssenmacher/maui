#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.OS;
using Android.Widget;
using Google.Android.Material.BottomNavigation;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform;
using Microsoft.Maui.Storage;
using AView = Android.Views.View;
using Environment = System.Environment;
using MauiSlider = Microsoft.Maui.Controls.Slider;

namespace AndroidDrawableResultDisposeLeakRepro;

public class MainPage : ContentPage
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly MethodInfo BottomNavigationSetMenuItemIconMethod =
		typeof(Microsoft.Maui.Controls.Platform.BottomNavigationViewUtils)
			.GetMethod("SetMenuItemIcon", BindingFlags.NonPublic | BindingFlags.Static)
		?? throw new InvalidOperationException("BottomNavigationViewUtils.SetMenuItemIcon was not found.");

	readonly Label _status;

	public MainPage()
	{
		_status = new Label
		{
			Text = "Running Android drawable result disposal leak repro...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		var result = await Task.Run(RunScenarios);
		var text = result.ToString();
		_status.Text = text;

		var resultsPath = System.IO.Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
		File.WriteAllText(resultsPath, text);
		Android.Util.Log.Info("AndroidDrawableResultDisposeLeakRepro", text);

		await Task.Delay(250);
		Process.KillProcess(Process.MyPid());
	}

	static ReproResult RunScenarios()
	{
		ReproResult? result = null;

		RunOnMainThread(() =>
		{
			var context = Platform.CurrentActivity
				?? throw new InvalidOperationException("No current Android Activity.");

			result = new ReproResult(
				RunBackgroundControl(context),
				RunCurrentBackgroundPath(context),
				RunSeekBarControl(context),
				RunCurrentSeekBarPath(context),
				RunBottomNavigationControl(context),
				RunCurrentBottomNavigationPath(context));
		});

		ForceGc();
		return result ?? throw new InvalidOperationException("The repro did not run.");
	}

	static ScenarioResult RunBackgroundControl(Context context)
	{
		var ledger = new ScenarioLedger("control-background-disposes-result");
		var provider = new TrackingImageSourceServiceProvider(ledger);

		for (var i = 0; i < Iterations; i++)
		{
			using var view = new AView(context);
			UpdateBackgroundAndDisposeResultAsync(view, new TrackingImageSource(i), provider)
				.GetAwaiter()
				.GetResult();
			view.Background = null;
		}

		return ledger.ToResult();
	}

	static ScenarioResult RunBottomNavigationControl(Context context)
	{
		var ledger = new ScenarioLedger("control-bottomnav-icon-disposes-result");
		var provider = new TrackingImageSourceServiceProvider(ledger);
		var mauiContext = new TrackingMauiContext(context, provider);

		for (var i = 0; i < Iterations; i++)
		{
			using var bottomView = new BottomNavigationView(context);
			var menuItem = bottomView.Menu.Add(0, i + 1, 0, $"Item {i}")!;
			UpdateBottomNavigationIconAndDisposeResultAsync(menuItem, new TrackingImageSource(i), mauiContext)
				.GetAwaiter()
				.GetResult();
			menuItem.SetIcon(null);
		}

		return ledger.ToResult();
	}

	static ScenarioResult RunCurrentBottomNavigationPath(Context context)
	{
		var ledger = new ScenarioLedger("leak-current-bottomnav-icon-helper");
		var provider = new TrackingImageSourceServiceProvider(ledger);
		var mauiContext = new TrackingMauiContext(context, provider);

		for (var i = 0; i < Iterations; i++)
		{
			using var bottomView = new BottomNavigationView(context);
			var menuItem = bottomView.Menu.Add(0, i + 1, 0, $"Item {i}")!;
			InvokeBottomNavigationSetMenuItemIconAsync(menuItem, new TrackingImageSource(i), mauiContext)
				.GetAwaiter()
				.GetResult();
			menuItem.SetIcon(null);
		}

		return ledger.ToResult();
	}

	static ScenarioResult RunCurrentBackgroundPath(Context context)
	{
		var ledger = new ScenarioLedger("leak-current-android-background-helper");
		var provider = new TrackingImageSourceServiceProvider(ledger);

		for (var i = 0; i < Iterations; i++)
		{
			using var view = new AView(context);
			view.UpdateBackgroundImageSourceAsync(new TrackingImageSource(i), provider)
				.GetAwaiter()
				.GetResult();
			view.Background = null;
		}

		return ledger.ToResult();
	}

	static ScenarioResult RunSeekBarControl(Context context)
	{
		var ledger = new ScenarioLedger("control-seekbar-thumb-disposes-result");
		var provider = new TrackingImageSourceServiceProvider(ledger);

		for (var i = 0; i < Iterations; i++)
		{
			using var seekBar = CreateSeekBar(context);
			var slider = CreateVirtualSlider(i);
			UpdateSeekBarThumbAndDisposeResultAsync(seekBar, slider, provider)
				.GetAwaiter()
				.GetResult();
			seekBar.SetThumb(null);
		}

		return ledger.ToResult();
	}

	static ScenarioResult RunCurrentSeekBarPath(Context context)
	{
		var ledger = new ScenarioLedger("leak-current-android-seekbar-thumb-helper");
		var provider = new TrackingImageSourceServiceProvider(ledger);

		for (var i = 0; i < Iterations; i++)
		{
			using var seekBar = CreateSeekBar(context);
			var slider = CreateVirtualSlider(i);
			seekBar.UpdateThumbImageSourceAsync(slider, provider)
				.GetAwaiter()
				.GetResult();
			seekBar.SetThumb(null);
		}

		return ledger.ToResult();
	}

	static async Task UpdateBackgroundAndDisposeResultAsync(
		AView platformView,
		IImageSource imageSource,
		IImageSourceServiceProvider provider)
	{
		var service = provider.GetRequiredImageSourceService(imageSource);
		var result = await service.GetDrawableAsync(imageSource, platformView.Context!);
		try
		{
			platformView.Background = result?.Value;
		}
		finally
		{
			platformView.Background = null;
			result?.Dispose();
		}
	}

	static async Task UpdateSeekBarThumbAndDisposeResultAsync(
		SeekBar seekBar,
		ISlider slider,
		IImageSourceServiceProvider provider)
	{
		var thumbImageSource = slider.ThumbImageSource;
		if (thumbImageSource is null)
			return;

		var service = provider.GetRequiredImageSourceService(thumbImageSource);
		var result = await service.GetDrawableAsync(thumbImageSource, seekBar.Context!);
		try
		{
			var thumbDrawable = result?.Value;
			if (thumbDrawable is not null)
				seekBar.SetThumb(thumbDrawable);
		}
		finally
		{
			seekBar.SetThumb(null);
			result?.Dispose();
		}
	}

	static async Task UpdateBottomNavigationIconAndDisposeResultAsync(
		IMenuItem menuItem,
		ImageSource source,
		IMauiContext context)
	{
		var services = context.Services;
		var provider = (IImageSourceServiceProvider?)services.GetService(typeof(IImageSourceServiceProvider))
			?? throw new InvalidOperationException("IImageSourceServiceProvider was not available.");
		var imageSourceService = provider.GetRequiredImageSourceService(source);

		var result = await imageSourceService.GetDrawableAsync(source, context.Context!);
		try
		{
			menuItem.SetIcon(result?.Value);
		}
		finally
		{
			menuItem.SetIcon(null);
			result?.Dispose();
		}
	}

	static Task InvokeBottomNavigationSetMenuItemIconAsync(
		IMenuItem menuItem,
		ImageSource source,
		IMauiContext context)
	{
		return (Task)BottomNavigationSetMenuItemIconMethod.Invoke(null, new object[] { menuItem, source, context })!;
	}

	static SeekBar CreateSeekBar(Context context)
	{
		return new SeekBar(context)
		{
			Max = 100,
			Progress = 50
		};
	}

	static MauiSlider CreateVirtualSlider(int iteration)
	{
		return new MauiSlider
		{
			Minimum = 0,
			Maximum = 100,
			Value = iteration % 100,
			ThumbImageSource = new TrackingImageSource(iteration)
		};
	}

	static Drawable CreateDrawable(int iteration)
	{
		return new ColorDrawable(Color.Rgb(
			(iteration * 37) % 255,
			(iteration * 67) % 255,
			(iteration * 97) % 255));
	}

	static void RunOnMainThread(Action action)
	{
		using var completed = new ManualResetEventSlim();
		Exception? exception = null;

		MainThread.BeginInvokeOnMainThread(() =>
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

	static void ForceGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(250);
		}
	}

	sealed class TrackingImageSource : ImageSource
	{
		public TrackingImageSource(int iteration)
		{
			Iteration = iteration;
		}

		public int Iteration { get; }

		public override bool IsEmpty => false;
	}

	sealed class TrackingImageSourceServiceProvider : IImageSourceServiceProvider
	{
		readonly TrackingImageSourceService _service;

		public TrackingImageSourceServiceProvider(ScenarioLedger ledger)
		{
			_service = new TrackingImageSourceService(ledger);
		}

		public IServiceProvider HostServiceProvider => this;

		public object? GetService(Type serviceType) => null;

		public IImageSourceService? GetImageSourceService(Type imageSource) =>
			imageSource == typeof(TrackingImageSource) ? _service : null;
	}

	sealed class TrackingMauiContext : IMauiContext
	{
		readonly TrackingServiceProvider _services;

		public TrackingMauiContext(Context context, IImageSourceServiceProvider imageProvider)
		{
			Context = context;
			_services = new TrackingServiceProvider(imageProvider);
		}

		public IServiceProvider Services => _services;

		public IMauiHandlersFactory Handlers => throw new NotSupportedException();

		public Context? Context { get; }
	}

	sealed class TrackingServiceProvider : IServiceProvider
	{
		readonly IImageSourceServiceProvider _imageProvider;

		public TrackingServiceProvider(IImageSourceServiceProvider imageProvider)
		{
			_imageProvider = imageProvider;
		}

		public object? GetService(Type serviceType) =>
			serviceType == typeof(IImageSourceServiceProvider)
				? _imageProvider
				: null;
	}

	sealed class TrackingImageSourceService : IImageSourceService<TrackingImageSource>
	{
		readonly ScenarioLedger _ledger;

		public TrackingImageSourceService(ScenarioLedger ledger)
		{
			_ledger = ledger;
		}

		public async Task<IImageSourceServiceResult?> LoadDrawableAsync(
			IImageSource imageSource,
			ImageView imageView,
			CancellationToken cancellationToken = default)
		{
			var result = await GetDrawableAsync(imageSource, imageView.Context!, cancellationToken);
			imageView.SetImageDrawable(result?.Value);
			return result;
		}

		public Task<IImageSourceServiceResult<Drawable>?> GetDrawableAsync(
			IImageSource imageSource,
			Context context,
			CancellationToken cancellationToken = default)
		{
			var source = (TrackingImageSource)imageSource;
			var payload = new NativeDrawablePayload(_ledger, source.Iteration, PayloadBytes);
			var drawable = CreateDrawable(source.Iteration);
			var result = new ImageSourceServiceResult(drawable, dispose: () =>
			{
				payload.Dispose();
				drawable.Dispose();
			});

			return Task.FromResult<IImageSourceServiceResult<Drawable>?>(result);
		}
	}

	sealed class NativeDrawablePayload : IDisposable
	{
		readonly ScenarioLedger _ledger;
		readonly int _bytes;
		IntPtr _buffer;
		bool _disposed;

		public NativeDrawablePayload(ScenarioLedger ledger, int iteration, int bytes)
		{
			_ledger = ledger;
			_bytes = bytes;
			_buffer = Marshal.AllocHGlobal(bytes);

			for (var i = 0; i < bytes; i += 4096)
				Marshal.WriteByte(_buffer, i, (byte)(iteration + i));

			_ledger.RecordAllocated(bytes);
		}

		public void Dispose()
		{
			if (_disposed)
				return;

			_disposed = true;
			Marshal.FreeHGlobal(_buffer);
			_buffer = IntPtr.Zero;
			_ledger.RecordDisposed(_bytes);
		}
	}

	sealed class ScenarioLedger
	{
		readonly string _name;
		long _allocatedBytes;
		long _disposedBytes;
		int _allocatedCount;
		int _disposedCount;

		public ScenarioLedger(string name)
		{
			_name = name;
		}

		public void RecordAllocated(int bytes)
		{
			_allocatedCount++;
			_allocatedBytes += bytes;
		}

		public void RecordDisposed(int bytes)
		{
			_disposedCount++;
			_disposedBytes += bytes;
		}

		public ScenarioResult ToResult()
		{
			return new ScenarioResult(
				_name,
				_allocatedCount,
				_disposedCount,
				_allocatedBytes,
				_disposedBytes);
		}
	}

	readonly record struct ScenarioResult(
		string Name,
		int AllocatedCount,
		int DisposedCount,
		long AllocatedBytes,
		long DisposedBytes)
	{
		public long LeakedBytes => AllocatedBytes - DisposedBytes;

		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.Append(Name);
			builder.Append(": results=");
			builder.Append(AllocatedCount);
			builder.Append('/');
			builder.Append(DisposedCount);
			builder.Append(", allocated=");
			builder.Append(FormatBytes(AllocatedBytes));
			builder.Append(", disposed=");
			builder.Append(FormatBytes(DisposedBytes));
			builder.Append(", leaked=");
			builder.Append(FormatBytes(LeakedBytes));
			return builder.ToString();
		}
	}

	readonly record struct ReproResult(
		ScenarioResult BackgroundControl,
		ScenarioResult BackgroundLeak,
		ScenarioResult SeekBarControl,
		ScenarioResult SeekBarLeak,
		ScenarioResult BottomNavigationControl,
		ScenarioResult BottomNavigationLeak)
	{
		public bool IsProven =>
			IsCleanControl(BackgroundControl) &&
			IsCleanControl(SeekBarControl) &&
			IsCleanControl(BottomNavigationControl) &&
			IsLeakingCurrentPath(BackgroundLeak) &&
			IsLeakingCurrentPath(SeekBarLeak) &&
			IsLeakingCurrentPath(BottomNavigationLeak);

		static bool IsCleanControl(ScenarioResult result) =>
			result.AllocatedCount == Iterations &&
			result.DisposedCount == Iterations &&
			result.LeakedBytes == 0;

		static bool IsLeakingCurrentPath(ScenarioResult result) =>
			result.AllocatedCount == Iterations &&
			result.DisposedCount == 0 &&
			result.LeakedBytes == Iterations * PayloadBytes;

		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.AppendLine(IsProven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine(BackgroundControl.ToString());
			builder.AppendLine(BackgroundLeak.ToString());
			builder.AppendLine(SeekBarControl.ToString());
			builder.AppendLine(SeekBarLeak.ToString());
			builder.AppendLine(BottomNavigationControl.ToString());
			builder.AppendLine(BottomNavigationLeak.ToString());
			builder.Append("payload-bytes-per-result=");
			builder.Append(PayloadBytes);
			builder.AppendLine();
			builder.Append("payload-bytes-per-leak-path=");
			builder.Append(Iterations * PayloadBytes);
			builder.AppendLine();
			builder.Append("app-data-directory=");
			builder.Append(FileSystem.AppDataDirectory);
			builder.AppendLine();
			builder.Append("dotnet-version=");
			builder.Append(Environment.Version);
			return builder.ToString();
		}
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024L * 1024L)
			return $"{bytes / 1024d / 1024d:0.0} MiB";

		if (bytes >= 1024L)
			return $"{bytes / 1024d:0.0} KiB";

		return $"{bytes} B";
	}
}
