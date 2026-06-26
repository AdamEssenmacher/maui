using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Maui;

namespace ImageSourceResultChurnLeakRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerAppliedImage = 2;

	static readonly Type ManagerType =
		typeof(ImageSourceServiceLoadResult).Assembly
			.GetType("Microsoft.Maui.ImageSourceServiceResultManager", throwOnError: true)!;

	static readonly MethodInfo BeginLoadMethod =
		ManagerType.GetMethod("BeginLoad", BindingFlags.Instance | BindingFlags.Public)!;

	static readonly MethodInfo CompleteLoadMethod =
		ManagerType.GetMethod(
			"CompleteLoad",
			BindingFlags.Instance | BindingFlags.Public,
			binder: null,
			types: new[] { typeof(IDisposable) },
			modifiers: null)!;

	static readonly MethodInfo ResetMethod =
		ManagerType.GetMethod("Reset", BindingFlags.Instance | BindingFlags.Public)!;

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineManagedBytes = GC.GetTotalMemory(true);

		var control = RunControl();
		var leak = RunLeak();

		ForceFullGc();
		var finalManagedBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerAppliedImage,
			baselineManagedBytes,
			finalManagedBytes,
			control,
			leak);
	}

	static ScenarioResult RunControl()
	{
		var ledger = new ScenarioLedger("control: version-aware manager disposes stale completions");
		var manager = new VersionAwareResultManager();

		for (var i = 0; i < Cycles; i++)
		{
			var slowVersion = manager.BeginLoad();
			var fastVersion = manager.BeginLoad();

			manager.CompleteLoad(fastVersion, CreateResult(ledger, i, ImageResultRole.AppliedFastImage));
			manager.CompleteLoad(slowVersion, CreateResult(ledger, i, ImageResultRole.StaleSlowImage));
		}

		manager.Reset();
		ForceFullGc();

		return ledger.ToResult();
	}

	static ScenarioResult RunLeak()
	{
		var ledger = new ScenarioLedger("leak: MAUI ImageSourceServiceResultManager accepts late stale completion");
		var manager = Activator.CreateInstance(ManagerType)!;

		for (var i = 0; i < Cycles; i++)
		{
			BeginLoadMethod.Invoke(manager, null);
			BeginLoadMethod.Invoke(manager, null);

			CompleteLoadMethod.Invoke(manager, new object?[] { CreateResult(ledger, i, ImageResultRole.AppliedFastImage) });
			CompleteLoadMethod.Invoke(manager, new object?[] { CreateResult(ledger, i, ImageResultRole.StaleSlowImage) });
		}

		ResetMethod.Invoke(manager, null);
		ForceFullGc();

		return ledger.ToResult();
	}

	static ImageSourceServiceLoadResult CreateResult(ScenarioLedger ledger, int cycle, ImageResultRole role)
	{
		var payloadBytes = PayloadMegabytesPerAppliedImage * 1024L * 1024L;
		var payload = new NativeImagePayload(ledger, cycle, role, payloadBytes);
		return new ImageSourceServiceLoadResult(dispose: payload.Dispose);
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

	enum ImageResultRole
	{
		AppliedFastImage,
		StaleSlowImage
	}

	sealed class VersionAwareResultManager
	{
		long _version;
		IDisposable? _sourceResult;
		CancellationTokenSource? _sourceCancellation;

		public long BeginLoad()
		{
			_version++;
			_sourceResult?.Dispose();
			_sourceResult = null;
			_sourceCancellation?.Cancel();
			_sourceCancellation?.Dispose();
			_sourceCancellation = new CancellationTokenSource();
			return _version;
		}

		public void CompleteLoad(long version, IDisposable? result)
		{
			if (version != _version)
			{
				result?.Dispose();
				return;
			}

			_sourceResult?.Dispose();
			_sourceResult = result;
			_sourceCancellation?.Dispose();
			_sourceCancellation = null;
		}

		public void Reset()
		{
			BeginLoad();
			CompleteLoad(_version, null);
		}
	}

	sealed class NativeImagePayload : IDisposable
	{
		readonly ScenarioLedger _ledger;
		readonly ImageResultRole _role;
		readonly long _bytes;
		IntPtr _buffer;
		bool _disposed;

		public NativeImagePayload(ScenarioLedger ledger, int cycle, ImageResultRole role, long bytes)
		{
			_ledger = ledger;
			_role = role;
			_bytes = bytes;
			_buffer = Marshal.AllocHGlobal(checked((nint)bytes));

			unsafe
			{
				var span = new Span<byte>((void*)_buffer, checked((int)bytes));
				for (var i = 0; i < span.Length; i += 4096)
					span[i] = (byte)(cycle + (int)role + i);
			}

			_ledger.RecordAllocated(role, bytes);
		}

		public void Dispose()
		{
			if (_disposed)
				return;

			_disposed = true;
			Marshal.FreeHGlobal(_buffer);
			_buffer = IntPtr.Zero;
			_ledger.RecordDisposed(_role, _bytes);
		}
	}

	sealed class ScenarioLedger
	{
		readonly string _name;
		long _allocatedAppliedBytes;
		long _allocatedStaleBytes;
		long _disposedAppliedBytes;
		long _disposedStaleBytes;
		int _allocatedAppliedCount;
		int _allocatedStaleCount;
		int _disposedAppliedCount;
		int _disposedStaleCount;

		public ScenarioLedger(string name)
		{
			_name = name;
		}

		public void RecordAllocated(ImageResultRole role, long bytes)
		{
			if (role == ImageResultRole.AppliedFastImage)
			{
				_allocatedAppliedCount++;
				_allocatedAppliedBytes += bytes;
			}
			else
			{
				_allocatedStaleCount++;
				_allocatedStaleBytes += bytes;
			}
		}

		public void RecordDisposed(ImageResultRole role, long bytes)
		{
			if (role == ImageResultRole.AppliedFastImage)
			{
				_disposedAppliedCount++;
				_disposedAppliedBytes += bytes;
			}
			else
			{
				_disposedStaleCount++;
				_disposedStaleBytes += bytes;
			}
		}

		public ScenarioResult ToResult()
		{
			return new ScenarioResult(
				_name,
				_allocatedAppliedCount,
				_disposedAppliedCount,
				_allocatedAppliedBytes,
				_disposedAppliedBytes,
				_allocatedStaleCount,
				_disposedStaleCount,
				_allocatedStaleBytes,
				_disposedStaleBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int AllocatedAppliedCount,
		int DisposedAppliedCount,
		long AllocatedAppliedBytes,
		long DisposedAppliedBytes,
		int AllocatedStaleCount,
		int DisposedStaleCount,
		long AllocatedStaleBytes,
		long DisposedStaleBytes)
	{
		public long LeakedAppliedBytes => AllocatedAppliedBytes - DisposedAppliedBytes;

		public long LeakedStaleBytes => AllocatedStaleBytes - DisposedStaleBytes;

		public long TotalLeakedBytes => LeakedAppliedBytes + LeakedStaleBytes;
	}

	internal sealed record ReproReport(
		int Cycles,
		int PayloadMegabytesPerAppliedImage,
		long BaselineManagedBytes,
		long FinalManagedBytes,
		ScenarioResult Control,
		ScenarioResult Leak)
	{
		public bool LeakProved =>
			Control.TotalLeakedBytes == 0 &&
			Leak.LeakedAppliedBytes == Cycles * PayloadMegabytesPerAppliedImage * 1024L * 1024L &&
			Leak.LeakedStaleBytes == 0;

		public string ToText()
		{
			return string.Join(Environment.NewLine,
				"ImageSourceResultChurnLeakRepro",
				$"Cycles: {Cycles}",
				$"Native-like payload per image result: {PayloadMegabytesPerAppliedImage} MiB",
				"Race shape: slow source A starts, fast source B replaces it and applies, then stale A completes late",
				$"Leak proved: {LeakProved}",
				string.Empty,
				FormatScenario(Control),
				string.Empty,
				FormatScenario(Leak),
				string.Empty,
				$"Managed heap baseline: {FormatBytes(BaselineManagedBytes)}",
				$"Managed heap final: {FormatBytes(FinalManagedBytes)}",
				$"Managed heap delta: {FormatBytes(FinalManagedBytes - BaselineManagedBytes)}");
		}

		static string FormatScenario(ScenarioResult result)
		{
			return string.Join(Environment.NewLine,
				$"Run: {result.Name}",
				$"  applied image results allocated/disposed: {result.AllocatedAppliedCount}/{result.DisposedAppliedCount}",
				$"  stale image results allocated/disposed: {result.AllocatedStaleCount}/{result.DisposedStaleCount}",
				$"  applied native-like bytes allocated: {FormatBytes(result.AllocatedAppliedBytes)}",
				$"  applied native-like bytes disposed: {FormatBytes(result.DisposedAppliedBytes)}",
				$"  applied native-like bytes leaked: {FormatBytes(result.LeakedAppliedBytes)}",
				$"  stale native-like bytes leaked: {FormatBytes(result.LeakedStaleBytes)}",
				$"  total native-like bytes leaked: {FormatBytes(result.TotalLeakedBytes)}");
		}

		static string FormatBytes(long bytes)
		{
			var sign = bytes < 0 ? "-" : string.Empty;
			var value = Math.Abs(bytes);

			if (value >= 1024L * 1024L * 1024L)
				return $"{sign}{value / 1024d / 1024d / 1024d:0.0} GiB";

			if (value >= 1024L * 1024L)
				return $"{sign}{value / 1024d / 1024d:0.0} MiB";

			if (value >= 1024L)
				return $"{sign}{value / 1024d:0.0} KiB";

			return $"{sign}{value} B";
		}
	}
}
