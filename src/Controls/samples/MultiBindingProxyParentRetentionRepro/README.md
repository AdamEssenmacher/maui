# MultiBinding Proxy Parent Retention Repro

This sample proves a removed `MultiBinding` can stay rooted by its hidden `ProxyElement`.

`MultiBinding.Apply(...)` creates a `ProxyElement` and sets `Parent = targetObject as Element` so child bindings can resolve resources and inherited context. `Element.SetParent(...)` registers the proxy's `OnParentResourcesChanged` delegate in the parent target's resource listener list. `MultiBinding.Unapply(...)` removes the proxy bindings and nulls `_proxyObject`, but it does not set `ProxyElement.Parent = null`, so a retained target keeps the proxy alive. The proxy keeps stale bindable-property contexts for the generated `mb-proxy*` properties, and those properties keep their instance `propertyChanged` delegate back to the removed `MultiBinding`.

The repro keeps target `Label`s alive in both runs:

- control: reflectively clears the hidden proxy parent before removing the binding;
- current: calls `RemoveBinding(Label.TextProperty)` using current MAUI behavior.

Each removed `MultiBinding` has a converter with a 1 MiB payload to model realistic converter/service/cache state.

Run:

```bash
dotnet build src/Controls/samples/MultiBindingProxyParentRetentionRepro/MultiBindingProxyParentRetentionRepro.csproj -f net10.0-maccatalyst -p:UseMaui=false -p:IncludeMacCatalystTargetFrameworks=true -m:1 -nr:false
open -W artifacts/bin/MultiBindingProxyParentRetentionRepro/Debug/net10.0-maccatalyst/maccatalyst-arm64/MultiBindingProxyParentRetentionRepro.app
cat /tmp/multibinding-proxy-parent-retention-results.txt
```
