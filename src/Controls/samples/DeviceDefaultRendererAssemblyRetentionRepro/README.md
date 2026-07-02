# Device DefaultRendererAssembly Retention Repro

This repro proves that `Microsoft.Maui.Controls.Device.DefaultRendererAssembly` can retain a collectible plugin/module assembly through a process-static compatibility renderer assembly slot.

The repro creates one collectible dynamic assembly with an 80 MiB static payload to model a large tenant/plugin renderer pack. It then assigns that assembly to the obsolete public static `Device.DefaultRendererAssembly` property, which compatibility registration uses when ordering renderer assembly scans.

`Device.DefaultRendererAssembly` is a single static assembly slot. In the control scenario, the repro clears the property before forcing GC. In current MAUI, the property is left intact.

## Result

Local run on 2026-07-02:

```text
Result: PROVEN

Control: Device.DefaultRendererAssembly cleared before forced GC
  Had default renderer assembly before collect: True
  Has default renderer assembly after collect: False
  Retained assembly: False
  Retained type: False
  Retained payload: False
  Retained payload bytes: 0
  Managed heap delta: 3,080 bytes

Current MAUI: Device.DefaultRendererAssembly left intact
  Had default renderer assembly before collect: True
  Has default renderer assembly after collect: True
  Retained assembly: True
  Retained type: True
  Retained payload: True
  Retained payload bytes: 83,886,080
  Managed heap delta: 83,896,968 bytes
```

## Commands

```bash
dotnet run --project src/Controls/samples/DeviceDefaultRendererAssemblyRetentionRepro/DeviceDefaultRendererAssemblyRetentionRepro.csproj -c Release -- --results=/tmp/device-default-renderer-assembly-retention-results.txt
```

Optional scale control:

```bash
dotnet run --project src/Controls/samples/DeviceDefaultRendererAssemblyRetentionRepro/DeviceDefaultRendererAssemblyRetentionRepro.csproj -c Release -- --payload-mib=160
```

## Tracking Check

Official `dotnet/maui` issue searches for `DefaultRendererAssembly memory leak`, `Device.DefaultRendererAssembly leak`, `DefaultRendererAssembly AssemblyLoadContext unload`, and `RegisterAll DefaultRendererAssembly retain assembly` found no exact tracking issue. Fork branch filters for `defaultrenderer`, `default-renderer`, `renderer-assembly`, `rendererassembly`, `device renderer`, and single-assembly retention terms found only unrelated concrete renderer repros, not this static assembly-slot class.
