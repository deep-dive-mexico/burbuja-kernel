# Scripted structured-response contributions

Implement `IScriptedStructuredResponse.TryRespond(StructuredLlmRequest, out JsonNode?)` and
register it with `IServiceCollection.AddSingleton<IScriptedStructuredResponse, YourResponse>()`
before building the host's service provider. `ScriptedLlmPlugin.ConfigureServices` injects the
entire enumerable into its existing provider; consumers do not register a second model provider.

Contributions run in registration order before built-ins. `true` claims the request, even with
a null response; `false` allows the next contribution or the normal plan/spec/supervisor
fallback. Each call must return a fresh JSON node and support concurrent dispatch. Cancellation
is checked before dispatch; contribution exceptions propagate. `CallWithUsageAsync` keeps the
provider's deterministic 100-input / 50-output fixture usage.

The plugin manifest loads into the default assembly context (`load: static`) so staged and
host-registered contributions share the same interface type. Include either the staged plugin
or its static fallback in host discovery, once. Provider ID: `scripted`; plugin ID: `Llm.Scripted`.
