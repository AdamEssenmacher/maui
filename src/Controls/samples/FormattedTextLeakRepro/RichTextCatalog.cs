namespace FormattedTextLeakRepro;

internal static class RichTextCatalog
{
	public const string CheckoutDisclosureKey = "CheckoutDisclosureText";
	public const string FinancingDisclosureKey = "FinancingDisclosureText";
	public const string PrivacyDisclosureKey = "PrivacyDisclosureText";

	static readonly string[] s_keys =
	[
		CheckoutDisclosureKey,
		FinancingDisclosureKey,
		PrivacyDisclosureKey
	];

	public static IReadOnlyList<string> Keys => s_keys;

	public static IEnumerable<KeyValuePair<string, FormattedString>> CreateApplicationResources()
	{
		yield return new(CheckoutDisclosureKey, CreateCheckoutDisclosure());
		yield return new(FinancingDisclosureKey, CreateFinancingDisclosure());
		yield return new(PrivacyDisclosureKey, CreatePrivacyDisclosure());
	}

	public static FormattedString GetShared(int index)
	{
		var key = s_keys[index % s_keys.Length];
		return (FormattedString)Application.Current!.Resources[key];
	}

	public static FormattedString CreateInline(int index)
	{
		return (index % s_keys.Length) switch
		{
			0 => CreateCheckoutDisclosure(),
			1 => CreateFinancingDisclosure(),
			_ => CreatePrivacyDisclosure()
		};
	}

	static FormattedString CreateCheckoutDisclosure()
	{
		return new FormattedString
		{
			Spans =
			{
				new Span { Text = "By continuing, you agree to the " },
				CreateLinkedSpan("Terms of Service"),
				new Span { Text = " and authorize account verification for this order." }
			}
		};
	}

	static FormattedString CreateFinancingDisclosure()
	{
		return new FormattedString
		{
			Spans =
			{
				new Span { Text = "Estimated payments include taxes and fees. " },
				CreateLinkedSpan("Review financing details"),
				new Span { Text = " before submitting." }
			}
		};
	}

	static FormattedString CreatePrivacyDisclosure()
	{
		return new FormattedString
		{
			Spans =
			{
				new Span { Text = "We use this information according to our " },
				CreateLinkedSpan("Privacy Notice"),
				new Span { Text = ". You can update communication choices later." }
			}
		};
	}

	static Span CreateLinkedSpan(string text)
	{
		var span = new Span
		{
			Text = text,
			TextColor = Color.FromArgb("#0F766E"),
			TextDecorations = TextDecorations.Underline,
			FontAttributes = FontAttributes.Bold
		};

		span.GestureRecognizers.Add(new TapGestureRecognizer
		{
			Command = new Command(() => { })
		});

		return span;
	}
}
