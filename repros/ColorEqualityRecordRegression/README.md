# Color Equality Record Regression Repro

This standalone console project demonstrates a .NET 11 preview regression in `Microsoft.Maui.Graphics.Color` equality after `Color` was changed from a plain class to a `record class`.

The repro does not require a MAUI source checkout or MAUI workloads. It references the published package:

```xml
<PackageReference Include="Microsoft.Maui.Graphics" Version="11.0.0-preview.4.26230.3" />
```

## Run

```bash
dotnet restore
dotnet run
```

The program intentionally exits with code `1` when the regression is reproduced. It exits with code `0` if equality follows the old ARGB-value contract.

## Expected Current Preview Output

The exact assembly location may differ, but the important part is that `ToInt()` equality is `True` while equality checks involving the base and derived `Color` instances are `False`.

```text
Microsoft.Maui.Graphics assembly:
  Version: 11.0.0.0
  Location: .../ColorEqualityRecordRegression/bin/Debug/net10.0/Microsoft.Maui.Graphics.dll

Red colors:
  Base type: Microsoft.Maui.Graphics.Color
  Derived type: DerivedColor
  Base ARGB: 0xFFFF0000
  Derived ARGB: 0xFFFF0000
  ToInt() equality: True
baseRed.Equals(derivedRed): False
derivedRed.Equals(baseRed): False
EqualityComparer<Color>.Default.Equals(baseRed, derivedRed): False
Dictionary<Color, string>.ContainsKey(derivedRed) after adding baseRed: False

Transparent colors:
  Base type: Microsoft.Maui.Graphics.Color
  Derived type: DerivedColor
  Base ARGB: 0x00000000
  Derived ARGB: 0x00000000
  ToInt() equality: True
Colors.Transparent.Equals(derivedTransparent): False

Result: REGRESSION REPRODUCED - same-ARGB base and derived Color instances compare unequal.
```

This demonstrates the record `EqualityContract` behavior: two `Color` instances with identical ARGB values compare unequal when their runtime record types differ. The helper subtype is also a `record class` because C# only allows records to inherit from records.
