using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Authentication;

#if ANDROID
using Android.Content;
#endif

namespace WebAuthenticatorCompletedOptionsRetentionLeakRepro;

internal static class ReproSession
{
	const int Attempts = 20;
	const int PayloadMegabytesPerAttempt = 8;
	const string CallbackScheme = "webauthcompletedrepro";

	static readonly Type ImplementationType =
		typeof(WebAuthenticator).Assembly.GetType("Microsoft.Maui.Authentication.WebAuthenticatorImplementation", throwOnError: true)!;

	static readonly FieldInfo? CurrentOptionsField =
		ImplementationType.GetField("currentOptions", BindingFlags.Instance | BindingFlags.NonPublic);

	static readonly FieldInfo? TcsResponseField =
		ImplementationType.GetField("tcsResponse", BindingFlags.Instance | BindingFlags.NonPublic);

	static readonly FieldInfo? RedirectUriField =
		ImplementationType.GetField("redirectUri", BindingFlags.Instance | BindingFlags.NonPublic) ??
		ImplementationType.GetField("currentRedirectUri", BindingFlags.Instance | BindingFlags.NonPublic);

	static readonly FieldInfo? CurrentViewControllerField =
		ImplementationType.GetField("currentViewController", BindingFlags.Instance | BindingFlags.NonPublic);

	static readonly FieldInfo? WasField =
		ImplementationType.GetField("was", BindingFlags.Instance | BindingFlags.NonPublic);

	static readonly FieldInfo? SfField =
		ImplementationType.GetField("sf", BindingFlags.Instance | BindingFlags.NonPublic);

	public static async Task<ReproReport> RunAsync()
	{
		ResetWebAuthenticatorState();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunCompletedCallbackScenarioAsync(clearSingletonAfterEachAttempt: true);
		var leak = await RunCompletedCallbackScenarioAsync(clearSingletonAfterEachAttempt: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Attempts,
			PayloadMegabytesPerAttempt,
			baselineBytes,
			finalBytes,
			control,
			leak);
	}

	static async Task<ScenarioResult> RunCompletedCallbackScenarioAsync(bool clearSingletonAfterEachAttempt)
	{
		ResetWebAuthenticatorState();
		var tracked = new List<TrackedAttempt>();
		var completedAttempts = 0;
		var acceptedCallbacks = 0;

		for (var i = 0; i < Attempts; i++)
		{
			var attempt = await CreateCompletedAttemptAsync(i);
			tracked.Add(attempt.Tracked);

			if (attempt.Completed)
				completedAttempts++;

			if (attempt.CallbackAccepted)
				acceptedCallbacks++;

			if (clearSingletonAfterEachAttempt)
				ResetWebAuthenticatorState();
			else
				MirrorNativeSessionCallbackCleanup();
		}

		ForceFullGc();

		return ScenarioResult.From(
			clearSingletonAfterEachAttempt
				? "control: completed callbacks with singleton fields cleared"
				: "current: completed callbacks retain last singleton options",
			tracked,
			completedAttempts,
			acceptedCallbacks);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static async Task<CompletedAttempt> CreateCompletedAttemptAsync(int attempt)
	{
		var payload = new AuthPayload(attempt, PayloadMegabytesPerAttempt * 1024L * 1024L);
		var decoder = new PayloadResponseDecoder(payload);
		var options = CreateOptions(attempt, decoder);

		var authTask = WebAuthenticator.Default.AuthenticateAsync(options);
		await WaitForPendingAuthenticationAsync();

		var callbackAccepted = SendPlatformCallback(attempt);
		var completed = false;

		try
		{
			await authTask.WaitAsync(TimeSpan.FromSeconds(5));
			completed = true;
		}
		catch
		{
			completed = false;
		}

		MirrorNativeSessionCallbackCleanup();

		return new CompletedAttempt(
			TrackedAttempt.Create(attempt, options, decoder, payload),
			completed,
			callbackAccepted);
	}

	static async Task WaitForPendingAuthenticationAsync()
	{
		for (var i = 0; i < 50; i++)
		{
			if (TcsResponseField?.GetValue(WebAuthenticator.Default) is TaskCompletionSource<WebAuthenticatorResult> tcs &&
				!tcs.Task.IsCompleted)
			{
				return;
			}

			await Task.Delay(50);
		}
	}

	static bool SendPlatformCallback(int attempt)
	{
		var callbackUri = new Uri($"{CallbackScheme}://callback?code=token-{attempt:0000}&state={attempt:0000}");

#if ANDROID
		var intent = new Intent(Intent.ActionView);
		intent.SetData(global::Android.Net.Uri.Parse(callbackUri.ToString()));
		return WebAuthenticator.Default.OnResume(intent);
#elif IOS || MACCATALYST || MACOS
		return WebAuthenticator.Default.OpenUrl(callbackUri);
#else
		return false;
#endif
	}

	static WebAuthenticatorOptions CreateOptions(int attempt, IWebAuthenticatorResponseDecoder decoder)
	{
		return new WebAuthenticatorOptions
		{
			Url = new Uri($"https://login.contoso.example/authorize?client_id=mobile-dashboard&state={attempt:0000}"),
			CallbackUrl = new Uri($"{CallbackScheme}://callback"),
			PrefersEphemeralWebBrowserSession = true,
			ResponseDecoder = decoder
		};
	}

	static void ResetWebAuthenticatorState()
	{
		var authenticator = WebAuthenticator.Default;

		CurrentOptionsField?.SetValue(authenticator, null);
		TcsResponseField?.SetValue(authenticator, null);
		RedirectUriField?.SetValue(authenticator, null);
		CurrentViewControllerField?.SetValue(authenticator, null);
		WasField?.SetValue(authenticator, null);
		SfField?.SetValue(authenticator, null);
	}

	static void MirrorNativeSessionCallbackCleanup()
	{
		var authenticator = WebAuthenticator.Default;

		CurrentViewControllerField?.SetValue(authenticator, null);
		WasField?.SetValue(authenticator, null);
		SfField?.SetValue(authenticator, null);
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

	internal sealed class PayloadResponseDecoder : IWebAuthenticatorResponseDecoder
	{
		readonly AuthPayload _payload;

		public PayloadResponseDecoder(AuthPayload payload)
		{
			_payload = payload;
		}

		public IDictionary<string, string>? DecodeResponse(Uri uri)
		{
			return new Dictionary<string, string>
			{
				["payloadCycle"] = _payload.Cycle.ToString(),
				["issuerCount"] = _payload.CachedIssuerMetadata.Count.ToString()
			};
		}
	}

	internal sealed class AuthPayload
	{
		public AuthPayload(int cycle, long payloadBytes)
		{
			Cycle = cycle;
			PayloadBytes = payloadBytes;
			WorkspaceBytes = new byte[payloadBytes];

			for (var i = 0; i < WorkspaceBytes.Length; i += 4096)
				WorkspaceBytes[i] = (byte)(cycle + i);

			CachedIssuerMetadata = Enumerable.Range(1, 40)
				.Select(index => new OidcCacheEntry(
					$"issuer-{cycle + 1:000}-{index:000}",
					$"https://login.contoso.example/tenant/{cycle + 1:000}/{index:000}",
					"cached discovery metadata, jwks, and policy state"))
				.ToArray();
		}

		public int Cycle { get; }

		public long PayloadBytes { get; }

		public byte[] WorkspaceBytes { get; }

		public IReadOnlyList<OidcCacheEntry> CachedIssuerMetadata { get; }
	}

	internal sealed record OidcCacheEntry(string Id, string Issuer, string State);

	internal sealed record CompletedAttempt(
		TrackedAttempt Tracked,
		bool Completed,
		bool CallbackAccepted);

	internal sealed record TrackedAttempt(
		int Attempt,
		WeakReference Options,
		WeakReference Decoder,
		WeakReference Payload,
		long PayloadBytes)
	{
		public static TrackedAttempt Create(int attempt, WebAuthenticatorOptions options, PayloadResponseDecoder decoder, AuthPayload payload)
		{
			return new TrackedAttempt(
				attempt,
				new WeakReference(options),
				new WeakReference(decoder),
				new WeakReference(payload),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedAttempts,
		int CompletedAttempts,
		int AcceptedCallbacks,
		int AliveOptions,
		int AliveDecoders,
		int AlivePayloads,
		long RetainedPayloadBytes)
	{
		public static ScenarioResult From(
			string name,
			IReadOnlyList<TrackedAttempt> attempts,
			int completedAttempts,
			int acceptedCallbacks)
		{
			var aliveOptions = 0;
			var aliveDecoders = 0;
			var alivePayloads = 0;
			long retainedPayloadBytes = 0;

			foreach (var attempt in attempts)
			{
				if (attempt.Options.IsAlive)
					aliveOptions++;

				if (attempt.Decoder.IsAlive)
					aliveDecoders++;

				if (attempt.Payload.IsAlive)
				{
					alivePayloads++;
					retainedPayloadBytes += attempt.PayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				attempts.Count,
				completedAttempts,
				acceptedCallbacks,
				aliveOptions,
				aliveDecoders,
				alivePayloads,
				retainedPayloadBytes);
		}
	}

	internal sealed record ReproReport(
		int Attempts,
		int PayloadMegabytesPerAttempt,
		long BaselineManagedBytes,
		long FinalManagedBytes,
		ScenarioResult Control,
		ScenarioResult Leak)
	{
		public bool LeakProved =>
			Control.CompletedAttempts == Attempts &&
			Control.AcceptedCallbacks == Attempts &&
			Control.AliveOptions == 0 &&
			Control.AliveDecoders == 0 &&
			Control.AlivePayloads == 0 &&
			Leak.CompletedAttempts == Attempts &&
			Leak.AcceptedCallbacks == Attempts &&
			Leak.AliveOptions == 1 &&
			Leak.AliveDecoders == 1 &&
			Leak.AlivePayloads == 1;

		public string ToText()
		{
			return string.Join(Environment.NewLine,
				"WebAuthenticatorCompletedOptionsRetentionLeakRepro",
				$"Attempts: {Attempts}",
				$"Payload per attempt: {PayloadMegabytesPerAttempt} MiB",
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

		string FormatScenario(ScenarioResult result)
		{
			var expectedPayload = result.TrackedAttempts == 0 ? 0 : result.TrackedAttempts * PayloadMegabytesPerAttempt * 1024L * 1024L;
			var retainedPercent = expectedPayload == 0 ? 0 : result.RetainedPayloadBytes * 100.0 / expectedPayload;

			return string.Join(Environment.NewLine,
				$"Run: {result.Name}",
				$"  tracked attempts: {result.TrackedAttempts}",
				$"  completed attempts: {result.CompletedAttempts}/{result.TrackedAttempts}",
				$"  accepted callbacks: {result.AcceptedCallbacks}/{result.TrackedAttempts}",
				$"  WebAuthenticatorOptions alive after full GC: {result.AliveOptions}/{result.TrackedAttempts}",
				$"  response decoders alive after full GC: {result.AliveDecoders}/{result.TrackedAttempts}",
				$"  decoder payloads alive after full GC: {result.AlivePayloads}/{result.TrackedAttempts}",
				$"  retained payload bytes: {FormatBytes(result.RetainedPayloadBytes)} ({retainedPercent:0.0}%)");
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
