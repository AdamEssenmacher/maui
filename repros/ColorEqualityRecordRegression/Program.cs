using System.Reflection;
using Microsoft.Maui.Graphics;

var graphicsAssembly = typeof(Color).Assembly;

var baseRed = new Color(1f, 0f, 0f, 1f);
var derivedRed = new DerivedColor(1f, 0f, 0f, 1f);

var baseTransparent = Colors.Transparent;
var derivedTransparent = new DerivedColor(0f, 0f, 0f, 0f);

var dictionary = new Dictionary<Color, string>
{
	[baseRed] = "base red"
};

var redToIntEqual = baseRed.ToInt() == derivedRed.ToInt();
var baseEqualsDerived = baseRed.Equals(derivedRed);
var derivedEqualsBase = derivedRed.Equals(baseRed);
var defaultComparerEquals = EqualityComparer<Color>.Default.Equals(baseRed, derivedRed);
var dictionaryContainsDerived = dictionary.ContainsKey(derivedRed);
var transparentToIntEqual = baseTransparent.ToInt() == derivedTransparent.ToInt();
var transparentEqualsDerived = baseTransparent.Equals(derivedTransparent);

PrintAssemblyInfo(graphicsAssembly);
Console.WriteLine();
PrintColorComparison("Red", baseRed, derivedRed, redToIntEqual);
Console.WriteLine($"baseRed.Equals(derivedRed): {baseEqualsDerived}");
Console.WriteLine($"derivedRed.Equals(baseRed): {derivedEqualsBase}");
Console.WriteLine($"EqualityComparer<Color>.Default.Equals(baseRed, derivedRed): {defaultComparerEquals}");
Console.WriteLine($"Dictionary<Color, string>.ContainsKey(derivedRed) after adding baseRed: {dictionaryContainsDerived}");
Console.WriteLine();
PrintColorComparison("Transparent", baseTransparent, derivedTransparent, transparentToIntEqual);
Console.WriteLine($"Colors.Transparent.Equals(derivedTransparent): {transparentEqualsDerived}");
Console.WriteLine();

var oldArgbValueContractHolds =
	redToIntEqual &&
	baseEqualsDerived &&
	derivedEqualsBase &&
	defaultComparerEquals &&
	dictionaryContainsDerived &&
	transparentToIntEqual &&
	transparentEqualsDerived;

if (oldArgbValueContractHolds)
{
	Console.WriteLine("Result: PASS - Color equality follows the old ARGB-value contract.");
	return 0;
}

Console.WriteLine("Result: REGRESSION REPRODUCED - same-ARGB base and derived Color instances compare unequal.");
return 1;

static void PrintAssemblyInfo(Assembly assembly)
{
	Console.WriteLine("Microsoft.Maui.Graphics assembly:");
	Console.WriteLine($"  Version: {assembly.GetName().Version}");
	Console.WriteLine($"  Location: {assembly.Location}");
}

static void PrintColorComparison(string label, Color baseColor, Color derivedColor, bool toIntEqual)
{
	Console.WriteLine($"{label} colors:");
	Console.WriteLine($"  Base type: {baseColor.GetType().FullName}");
	Console.WriteLine($"  Derived type: {derivedColor.GetType().FullName}");
	Console.WriteLine($"  Base ARGB: 0x{unchecked((uint)baseColor.ToInt()):X8}");
	Console.WriteLine($"  Derived ARGB: 0x{unchecked((uint)derivedColor.ToInt()):X8}");
	Console.WriteLine($"  ToInt() equality: {toIntEqual}");
}

sealed record class DerivedColor : Color
{
	public DerivedColor(float red, float green, float blue, float alpha)
		: base(red, green, blue, alpha)
	{
	}
}
