using System.Reflection;
using Microsoft.Maui.Authentication;

namespace WebAuthenticatorOptionsRetentionLeakRepro;

internal static class ReproSession
{
	const int Attempts = 20;
	const int PayloadMegabytesPerAttempt = 8;
	const string UnsupportedCallbackScheme = "webauthretentionrepro";

	static readonly Type ImplementationType =
		typeof(WebAuthenticator).Assembly.GetType("Microsoft.Maui.Authentication.WebAuthenticatorImplementation", throwOnError: true)!;

	static readonly MethodInfo? VerifySchemeMethod =
		ImplementationType.GetMethod("VerifyHasUrlSchemeOrDoesntRequire", BindingFlags.Static | BindingFlags.NonPublic);

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

	public static ReproReport Run()
	{
		ResetWebAuthenticatorState();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunControl();
		var leak = RunLeak();

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Attempts,
			PayloadMegabytesPerAttempt,
			CanUseFailedPreflightPath(),
			baselineBytes,
			finalBytes,
			control,
			leak);
	}

	static ScenarioResult RunControl()
	{
		ResetWebAuthenticatorState();
		var tracked = new List<TrackedAttempt>();

		for (var i = 0; i < Attempts; i++)
			CreateControlAttempt(tracked, i);

		ForceFullGc();
		return ScenarioResult.From("control: options and decoder not passed to WebAuthenticator singleton", tracked);
	}

	static ScenarioResult RunLeak()
	{
		ResetWebAuthenticatorState();
		var tracked = new List<TrackedAttempt>();

		if (!CanUseFailedPreflightPath())
		{
			return ScenarioResult.From("leak: failed preflight path not available on this platform", tracked);
		}

		var failedAttempts = 0;
		for (var i = 0; i < Attempts; i++)
		{
			if (CreateLeakAttempt(tracked, i))
				failedAttempts++;
		}

		ForceFullGc();
		return ScenarioResult.From("leak: failed WebAuthenticator validation retains last options", tracked, failedAttempts);
	}

	static void CreateControlAttempt(List<TrackedAttempt> tracked, int attempt)
	{
		var payload = new AuthPayload(attempt, PayloadMegabytesPerAttempt * 1024L * 1024L);
		var decoder = new PayloadResponseDecoder(payload);
		var options = CreateOptions(attempt, decoder);

		tracked.Add(TrackedAttempt.Create(attempt, options, decoder, payload));
	}

	static bool CreateLeakAttempt(List<TrackedAttempt> tracked, int attempt)
	{
		var payload = new AuthPayload(attempt, PayloadMegabytesPerAttempt * 1024L * 1024L);
		var decoder = new PayloadResponseDecoder(payload);
		var options = CreateOptions(attempt, decoder);
		var failed = false;

		try
		{
			WebAuthenticator.Default.AuthenticateAsync(options).GetAwaiter().GetResult();
		}
		catch (InvalidOperationException)
		{
			failed = true;
		}

		tracked.Add(TrackedAttempt.Create(attempt, options, decoder, payload));
		return failed;
	}

	static WebAuthenticatorOptions CreateOptions(int attempt, IWebAuthenticatorResponseDecoder decoder)
	{
		return new WebAuthenticatorOptions
		{
			Url = new Uri($"https://login.contoso.example/authorize?client_id=mobile-dashboard&state={attempt:0000}"),
			CallbackUrl = new Uri($"{UnsupportedCallbackScheme}://callback"),
			PrefersEphemeralWebBrowserSession = true,
			ResponseDecoder = decoder
		};
	}

	static bool CanUseFailedPreflightPath()
	{
#if ANDROID
		return true;
#else
		if (VerifySchemeMethod is null)
			return false;

		var result = VerifySchemeMethod.Invoke(null, new object[] { UnsupportedCallbackScheme });
		return result is bool allowed && !allowed;
#endif
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
				["payloadCycle"] = _payload.Cycle.ToString()
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
		int FailedAttempts,
		int AliveOptions,
		int AliveDecoders,
		int AlivePayloads,
		long RetainedPayloadBytes)
	{
		public static ScenarioResult From(string name, IReadOnlyList<TrackedAttempt> attempts, int failedAttempts = 0)
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
				failedAttempts,
				aliveOptions,
				aliveDecoders,
				alivePayloads,
				retainedPayloadBytes);
		}
	}

	internal sealed record ReproReport(
		int Attempts,
		int PayloadMegabytesPerAttempt,
		bool FailedPreflightPathAvailable,
		long BaselineManagedBytes,
		long FinalManagedBytes,
		ScenarioResult Control,
		ScenarioResult Leak)
	{
		public bool LeakProved =>
			FailedPreflightPathAvailable &&
			Control.AliveOptions == 0 &&
			Control.AliveDecoders == 0 &&
			Control.AlivePayloads == 0 &&
			Leak.FailedAttempts == Attempts &&
			Leak.AliveOptions == 1 &&
			Leak.AliveDecoders == 1 &&
			Leak.AlivePayloads == 1;

		public string ToText()
		{
			return string.Join(Environment.NewLine,
				"WebAuthenticatorOptionsRetentionLeakRepro",
				$"Attempts: {Attempts}",
				$"Payload per attempt: {PayloadMegabytesPerAttempt} MiB",
				$"Failed preflight path available: {FailedPreflightPathAvailable}",
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
				$"  failed preflight attempts: {result.FailedAttempts}/{result.TrackedAttempts}",
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
