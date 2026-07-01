# XAML TypeConversionExtensions MemberInfo Cache Retention Repro

This repro proves that runtime XAML property conversion can retain collectible dynamic types through the static `TypeConversionExtensions.s_converterCache` dictionary.

The repro creates 80 collectible dynamic root types. Each type has a public `Number` property and a 1 MiB static payload to model a small plugin, tenant module, or dynamic UI package. It then invokes the real `XamlLoader.Load(object, string, Assembly, bool)` path with XAML that sets `Number="123"`.

During property application, `ApplyPropertiesVisitor.TrySetProperty(...)` passes the dynamic `PropertyInfo` to `TypeConversionExtensions.ConvertTo(...)`. `TryGetTypeConverter(MemberInfo, ...)` stores that `PropertyInfo` in the static `s_converterCache` even when the member has no custom converter. The strong `MemberInfo` key keeps the dynamic declaring `Type` and its static payload alive.

The repro clears `XamlParser.s_allowImplicitXmlns` in both scenarios so the known C430 assembly-keyed cache does not affect the result. In the control scenario, it also clears `s_converterCache` before forcing GC. In current MAUI, the converter cache is left intact.

## Result

Local run on 2026-07-01:

```text
Control cleared converter cache: cache=0, assemblies=0/80, types=0/80, payloads=0/80, retainedPayload=0.0 MiB
Current MAUI: cache=81, assemblies=0/80, types=80/80, payloads=80/80, retainedPayload=80.0 MiB
```

## Commands

```bash
dotnet run --project src/Controls/samples/XamlTypeConversionMemberInfoCacheRetentionRepro/XamlTypeConversionMemberInfoCacheRetentionRepro.csproj -c Release -- --results=/tmp/xaml-typeconversion-memberinfo-cache-retention-results.txt
```

Optional scale controls:

```bash
dotnet run --project src/Controls/samples/XamlTypeConversionMemberInfoCacheRetentionRepro/XamlTypeConversionMemberInfoCacheRetentionRepro.csproj -c Release -- --count=160 --payload-mib=1
```

## Tracking Check

Official `dotnet/maui` issue searches for `TypeConversionExtensions s_converterCache memory leak`, `TypeConverter cache MemberInfo memory leak`, and `XAML TypeConverter memory leak` found no exact tracking issue. Fork branch filters for `typeconverter`, `type-converter`, `converter-cache`, `memberinfo`, and `xaml converter` found no existing repro branch.
