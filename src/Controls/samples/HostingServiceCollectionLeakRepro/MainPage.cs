#nullable enable

using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.Storage;
#if IOS || MACCATALYST
using UIKit;
#endif

namespace HostingServiceCollectionLeakRepro;

public sealed class MainPage : ContentPage
{
	const int Iterations = 60;
	const int PayloadBytes = 1024 * 1024;

	readonly Label _status;

	public MainPage()
	{
		_status = new Label
		{
			Text = "Running hosting service collection leak repro...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		ReproResult result;

		try
		{
			result = await RunScenariosAsync();
		}
		catch (Exception ex)
		{
			var failure = "RESULT: ERROR" + Environment.NewLine + ex;
			_status.Text = failure;
			await WriteResultsAsync(failure);
			await Task.Delay(250);
			Environment.Exit(3);
			return;
		}

		var text = result.ToString();
		_status.Text = text;
		await WriteResultsAsync(text);
		await Task.Delay(250);
		Environment.Exit(result.IsProven ? 0 : 2);
	}

	static async Task<ReproResult> RunScenariosAsync()
	{
		var before = StaticCounts.Read();
		var control = await RunUnresolvedControlAsync();
		var afterControl = StaticCounts.Read();
		var handlerLeak = await RunResolvedHandlerCollectionAsync();
		var afterHandler = StaticCounts.Read();
		var imageLeak = await RunResolvedImageSourceCollectionAsync();
		var afterImage = StaticCounts.Read();

		return new ReproResult(before, control, afterControl, handlerLeak, afterHandler, imageLeak, afterImage);
	}

	static async Task<ScenarioResult> RunUnresolvedControlAsync()
	{
		var payloadRefs = new List<WeakReference>();
		var collectionRefs = new List<WeakReference>();

		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			for (var i = 0; i < Iterations; i++)
			{
				CreateUnresolvedHost(payloadRefs);
			}
		});

		await WaitAndCollectAsync();

		return new ScenarioResult("unresolved-control", CountAlive(payloadRefs), CountAlive(collectionRefs), Iterations * 2);
	}

	static async Task<ScenarioResult> RunResolvedHandlerCollectionAsync()
	{
		var payloadRefs = new List<WeakReference>();
		var collectionRefs = new List<WeakReference>();

		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			for (var i = 0; i < Iterations; i++)
			{
				CreateResolvedHandlerHost(payloadRefs, collectionRefs);
			}
		});

		await WaitAndCollectAsync();

		return new ScenarioResult("resolved-handler-collection", CountAlive(payloadRefs), CountAlive(collectionRefs), Iterations);
	}

	static async Task<ScenarioResult> RunResolvedImageSourceCollectionAsync()
	{
		var payloadRefs = new List<WeakReference>();
		var collectionRefs = new List<WeakReference>();

		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			for (var i = 0; i < Iterations; i++)
			{
				CreateResolvedImageSourceHost(payloadRefs, collectionRefs);
			}
		});

		await WaitAndCollectAsync();

		return new ScenarioResult("resolved-image-source-collection", CountAlive(payloadRefs), CountAlive(collectionRefs), Iterations);
	}

	static void CreateUnresolvedHost(List<WeakReference> payloadRefs)
	{
		var handlerPayload = new Payload(PayloadBytes);
		var imagePayload = new Payload(PayloadBytes);
		var builder = MauiApp.CreateBuilder(useDefaults: false);

		builder.ConfigureMauiHandlers(handlers =>
		{
			handlers.AddHandler<ReproElement>(_ =>
			{
				handlerPayload.Touch();
				return new ReproHandler();
			});
		});

		builder.ConfigureImageSources(services =>
		{
			services.AddService<ReproImageSource>(_ =>
			{
				imagePayload.Touch();
				return new ReproImageSourceService();
			});
		});

		using (builder.Build())
		{
		}

		payloadRefs.Add(new WeakReference(handlerPayload));
		payloadRefs.Add(new WeakReference(imagePayload));
	}

	static void CreateResolvedHandlerHost(List<WeakReference> payloadRefs, List<WeakReference> collectionRefs)
	{
		var payload = new Payload(PayloadBytes);
		var builder = MauiApp.CreateBuilder(useDefaults: false);

		builder.ConfigureMauiHandlers(handlers =>
		{
			handlers.AddHandler<ReproElement>(_ =>
			{
				payload.Touch();
				return new ReproHandler();
			});
		});

		using (var app = builder.Build())
		{
			var collection = app.Services.GetRequiredService<IMauiHandlersFactory>().GetCollection();
			collectionRefs.Add(new WeakReference(collection));
		}

		payloadRefs.Add(new WeakReference(payload));
	}

	static void CreateResolvedImageSourceHost(List<WeakReference> payloadRefs, List<WeakReference> collectionRefs)
	{
		var payload = new Payload(PayloadBytes);
		var builder = MauiApp.CreateBuilder(useDefaults: false);

		builder.ConfigureImageSources(services =>
		{
			services.AddService<ReproImageSource>(_ =>
			{
				payload.Touch();
				return new ReproImageSourceService();
			});
		});

		using (var app = builder.Build())
		{
			var collection = app.Services.GetRequiredService<IImageSourceServiceCollection>();
			_ = app.Services.GetRequiredService<IImageSourceServiceProvider>();
			collectionRefs.Add(new WeakReference(collection));
		}

		payloadRefs.Add(new WeakReference(payload));
	}

	static async Task WaitAndCollectAsync()
	{
		await Task.Delay(250);
		await Task.Run(ForceGc);
		await Task.Delay(100);
		await Task.Run(ForceGc);
	}

	static void ForceGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(100);
		}
	}

	static int CountAlive(List<WeakReference> refs)
	{
		var count = 0;

		foreach (var reference in refs)
		{
			if (reference.IsAlive)
				count++;
		}

		return count;
	}

	static async Task WriteResultsAsync(string text)
	{
		var paths = new[]
		{
			Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt"),
			Path.Combine(Path.GetTempPath(), "hostingservicecollectionleakrepro-results.txt")
		};

		foreach (var path in paths)
		{
			try
			{
				var directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrWhiteSpace(directory))
					Directory.CreateDirectory(directory);

				await File.WriteAllTextAsync(path, text);
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex);
			}
		}

		Console.WriteLine(text);
	}

	sealed class ReproElement : Element
	{
	}

	sealed class ReproHandler : IElementHandler
	{
		public object? PlatformView => null;

		public IElement? VirtualView { get; private set; }

		public IMauiContext? MauiContext { get; private set; }

		public void SetMauiContext(IMauiContext mauiContext) => MauiContext = mauiContext;

		public void SetVirtualView(IElement view) => VirtualView = view;

		public void UpdateValue(string property)
		{
		}

		public void Invoke(string command, object? args = null)
		{
		}

		public void DisconnectHandler()
		{
			VirtualView = null;
			MauiContext = null;
		}
	}

	sealed class ReproImageSource : IImageSource
	{
		public bool IsEmpty => false;
	}

	sealed class ReproImageSourceService : IImageSourceService<ReproImageSource>
	{
#if IOS || MACCATALYST
		public Task<IImageSourceServiceResult<UIImage>?> GetImageAsync(
			IImageSource imageSource,
			float scale = 1,
			CancellationToken cancellationToken = default)
		{
			return Task.FromResult<IImageSourceServiceResult<UIImage>?>(null);
		}
#endif
	}

	sealed class Payload
	{
		readonly byte[] _data;
		int _ticks;

		public Payload(int bytes)
		{
			_data = new byte[bytes];
			_data[0] = 123;
		}

		public void Touch()
		{
			_ticks++;
			if (_ticks == int.MaxValue)
				_ticks = _data[0];
		}
	}

	readonly record struct StaticCounts(int HandlerCollections, int ImageSourceCollections)
	{
		public static StaticCounts Read()
		{
			return new StaticCounts(
				ReadCount(typeof(MauiApp).Assembly, "Microsoft.Maui.Hosting.Internal.RegisteredHandlerServiceTypeSet"),
				ReadCount(typeof(MauiApp).Assembly, "Microsoft.Maui.Hosting.ImageSourceToImageSourceServiceTypeMapping"));
		}

		static int ReadCount(Assembly assembly, string typeName)
		{
			var type = assembly.GetType(typeName)
				?? throw new InvalidOperationException($"Missing type {typeName}.");
			var field = type.GetField("s_instances", BindingFlags.NonPublic | BindingFlags.Static)
				?? throw new InvalidOperationException($"Missing {typeName}.s_instances.");
			var dictionary = field.GetValue(null)
				?? throw new InvalidOperationException($"{typeName}.s_instances was null.");
			var count = dictionary.GetType().GetProperty("Count")
				?? throw new InvalidOperationException($"{typeName}.s_instances has no Count property.");

			return (int)(count.GetValue(dictionary) ?? 0);
		}

		public override string ToString() => $"handler-collections={HandlerCollections}, image-source-collections={ImageSourceCollections}";
	}

	readonly record struct ScenarioResult(string Name, int PayloadsAlive, int CollectionsAlive, int Total)
	{
		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.Append(Name);
			builder.Append(": payloads=");
			builder.Append(PayloadsAlive);
			builder.Append('/');
			builder.Append(Total);
			builder.Append(", collections=");
			builder.Append(CollectionsAlive);
			builder.Append('/');
			builder.Append(Total);
			return builder.ToString();
		}
	}

	readonly record struct ReproResult(
		StaticCounts Before,
		ScenarioResult Control,
		StaticCounts AfterControl,
		ScenarioResult HandlerLeak,
		StaticCounts AfterHandler,
		ScenarioResult ImageLeak,
		StaticCounts AfterImage)
	{
		static int LeakThreshold => Iterations / 2;

		int HandlerStaticDelta => AfterHandler.HandlerCollections - AfterControl.HandlerCollections;

		int ImageStaticDelta => AfterImage.ImageSourceCollections - AfterHandler.ImageSourceCollections;

		public bool IsProven =>
			Control.PayloadsAlive == 0 &&
			Control.CollectionsAlive == 0 &&
			HandlerLeak.PayloadsAlive >= LeakThreshold &&
			HandlerLeak.CollectionsAlive >= LeakThreshold &&
			HandlerStaticDelta >= LeakThreshold &&
			ImageLeak.PayloadsAlive >= LeakThreshold &&
			ImageLeak.CollectionsAlive >= LeakThreshold &&
			ImageStaticDelta >= LeakThreshold;

		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.AppendLine(IsProven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("before: " + Before);
			builder.AppendLine(Control.ToString());
			builder.AppendLine("after-control: " + AfterControl);
			builder.AppendLine(HandlerLeak.ToString());
			builder.AppendLine("after-handler: " + AfterHandler);
			builder.Append("handler-static-delta=");
			builder.Append(HandlerStaticDelta);
			builder.AppendLine();
			builder.AppendLine(ImageLeak.ToString());
			builder.AppendLine("after-image: " + AfterImage);
			builder.Append("image-source-static-delta=");
			builder.Append(ImageStaticDelta);
			builder.AppendLine();
			builder.Append("payload-bytes-per-leak-scenario=");
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
}
