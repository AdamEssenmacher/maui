using Foundation;
using Microsoft.Maui;
using UIKit;

namespace StreamImageSourceUndisposedStreamLeakRepro;

static class StreamImageSourceUndisposedStreamProbe
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly byte[] PngBytes = Convert.FromBase64String(
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/lw9T+wAAAABJRU5ErkJggg==");

	public static async Task<ProbeResult> RunAsync()
	{
		PooledPayloadStream.ClearLeases();

		var service = new StreamImageSourceService();
		var controlRefs = new List<LeaseRefs>(Iterations);
		var currentRefs = new List<LeaseRefs>(Iterations);

		for (var i = 0; i < Iterations; i++)
			controlRefs.Add(await CreateControlScenarioAsync(i));

		for (var i = 0; i < Iterations; i++)
			currentRefs.Add(await CreateCurrentScenarioAsync(service, i));

		ForceCollect();

		return new ProbeResult(
			Iterations,
			PayloadBytes,
			CountAlive(controlRefs, static r => r.Stream),
			CountAlive(controlRefs, static r => r.Payload),
			CountAlive(currentRefs, static r => r.Stream),
			CountAlive(currentRefs, static r => r.Payload),
			PooledPayloadStream.ActiveLeaseCount,
			GC.GetTotalMemory(forceFullCollection: true));
	}

	static async Task<LeaseRefs> CreateControlScenarioAsync(int index)
	{
		var source = new LeasedStreamImageSource(index, PngBytes, PayloadBytes);
		using var stream = (PooledPayloadStream)await source.GetStreamAsync();
		var refs = new LeaseRefs(new WeakReference<PooledPayloadStream>(stream), new WeakReference<Payload>(stream.Payload));

		using var data = NSData.FromStream(stream)
			?? throw new InvalidOperationException("Failed to read control PNG data.");
		using var image = UIImage.LoadFromData(data)
			?? throw new InvalidOperationException("Failed to decode control PNG.");

		await Task.Yield();
		return refs;
	}

	static async Task<LeaseRefs> CreateCurrentScenarioAsync(StreamImageSourceService service, int index)
	{
		var source = new LeasedStreamImageSource(index + Iterations, PngBytes, PayloadBytes);
		var result = await service.GetImageAsync(source);
		result?.Dispose();

		var stream = source.LastStream
			?? throw new InvalidOperationException("StreamImageSourceService did not request a stream.");
		var refs = new LeaseRefs(new WeakReference<PooledPayloadStream>(stream), new WeakReference<Payload>(stream.Payload));

		await Task.Yield();
		return refs;
	}

	static int CountAlive<T>(List<LeaseRefs> refs, Func<LeaseRefs, WeakReference<T>> selector)
		where T : class
	{
		var count = 0;
		foreach (var item in refs)
		{
			if (selector(item).TryGetTarget(out _))
				count++;
		}

		return count;
	}

	static void ForceCollect()
	{
		for (var i = 0; i < 8; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(50);
		}
	}

	sealed class LeasedStreamImageSource : IStreamImageSource
	{
		readonly int _id;
		readonly byte[] _imageBytes;
		readonly int _payloadBytes;

		public LeasedStreamImageSource(int id, byte[] imageBytes, int payloadBytes)
		{
			_id = id;
			_imageBytes = imageBytes;
			_payloadBytes = payloadBytes;
		}

		public bool IsEmpty => false;

		public PooledPayloadStream? LastStream { get; private set; }

		public Task<Stream> GetStreamAsync(CancellationToken cancellationToken = default)
		{
			LastStream = new PooledPayloadStream(_id, _imageBytes, _payloadBytes);
			return Task.FromResult<Stream>(LastStream);
		}
	}

	sealed class PooledPayloadStream : Stream
	{
		static readonly object Sync = new();
		static readonly HashSet<PooledPayloadStream> ActiveLeases = new();

		readonly byte[] _imageBytes;
		int _position;
		bool _disposed;
		Payload? _payload;

		public PooledPayloadStream(int id, byte[] imageBytes, int payloadBytes)
		{
			_imageBytes = imageBytes;
			_payload = new Payload(id, payloadBytes);

			lock (Sync)
				ActiveLeases.Add(this);
		}

		public Payload Payload => _payload ?? throw new ObjectDisposedException(nameof(PooledPayloadStream));

		public static int ActiveLeaseCount
		{
			get
			{
				lock (Sync)
					return ActiveLeases.Count;
			}
		}

		public static void ClearLeases()
		{
			lock (Sync)
				ActiveLeases.Clear();
		}

		public override bool CanRead => !_disposed;
		public override bool CanSeek => !_disposed;
		public override bool CanWrite => false;
		public override long Length => _imageBytes.Length;
		public override long Position
		{
			get => _position;
			set => _position = checked((int)value);
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);

			var remaining = _imageBytes.Length - _position;
			if (remaining <= 0)
				return 0;

			var actual = Math.Min(count, remaining);
			Buffer.BlockCopy(_imageBytes, _position, buffer, offset, actual);
			_position += actual;
			return actual;
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);

			var next = origin switch
			{
				SeekOrigin.Begin => offset,
				SeekOrigin.Current => _position + offset,
				SeekOrigin.End => _imageBytes.Length + offset,
				_ => throw new ArgumentOutOfRangeException(nameof(origin))
			};

			if (next < 0 || next > _imageBytes.Length)
				throw new IOException("Seek outside stream bounds.");

			_position = checked((int)next);
			return _position;
		}

		public override void SetLength(long value) =>
			throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) =>
			throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (!_disposed && disposing)
			{
				lock (Sync)
					ActiveLeases.Remove(this);

				_payload = null;
			}

			_disposed = true;
			base.Dispose(disposing);
		}
	}

	sealed class Payload
	{
		readonly byte[] _bytes;

		public Payload(int id, int size)
		{
			Id = id;
			_bytes = new byte[size];
			_bytes[0] = (byte)(id % 251);
			_bytes[^1] = (byte)((id + 17) % 251);
		}

		public int Id { get; }
	}

	sealed record LeaseRefs(
		WeakReference<PooledPayloadStream> Stream,
		WeakReference<Payload> Payload);
}

sealed record ProbeResult(
	int Iterations,
	int PayloadBytes,
	int ControlStreamsRetained,
	int ControlPayloadsRetained,
	int CurrentStreamsRetained,
	int CurrentPayloadsRetained,
	int ActiveStreamLeases,
	long ManagedHeapBytes)
{
	public bool ProvedLeak =>
		ControlStreamsRetained == 0 &&
		ControlPayloadsRetained == 0 &&
		CurrentStreamsRetained == Iterations &&
		CurrentPayloadsRetained == Iterations &&
		ActiveStreamLeases == Iterations;

	public string ToReport()
	{
		var retainedPayloadMiB = CurrentPayloadsRetained * PayloadBytes / 1024.0 / 1024.0;
		var heapMiB = ManagedHeapBytes / 1024.0 / 1024.0;

		return string.Join(Environment.NewLine, new[]
		{
			"StreamImageSourceUndisposedStreamLeakRepro",
			$"Iterations: {Iterations}",
			$"Payload bytes per stream lease: {PayloadBytes}",
			$"Control retained streams: {ControlStreamsRetained}/{Iterations}",
			$"Control retained payloads: {ControlPayloadsRetained}/{Iterations}",
			$"Current retained streams: {CurrentStreamsRetained}/{Iterations}",
			$"Current retained payloads: {CurrentPayloadsRetained}/{Iterations}",
			$"Current active stream leases: {ActiveStreamLeases}/{Iterations}",
			$"Retained payload estimate: {retainedPayloadMiB:F1} MiB",
			$"Managed heap after proof: {heapMiB:F1} MiB",
			$"Proved leak: {ProvedLeak}"
		});
	}
}
