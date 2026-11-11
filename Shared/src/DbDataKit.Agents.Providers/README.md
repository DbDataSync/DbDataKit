# DbDataKit.Agents.Providers

The model provider adapters of the agent runtime (plan 27a AG04, AG05). Two `IChatClient`s over HTTP, written against the documented request and response shapes (no provider package):

- `AnthropicChatClient`: Anthropic's Messages API (`/v1/messages`, `x-api-key`, `anthropic-version`), tool use and streaming.
- `OpenAiCompatibleChatClient`: the chat completions API (`/chat/completions`) of OpenAI, Microsoft Foundry (`api-key` header), Ollama (`/v1`) and local servers, tool calls and streaming.

`ProviderClients.Create` makes the client for a `model-provider/1` definition (`DbDataKit.Agents`, `ModelProviders.cs`) and a key resolved from its secret reference; `ProviderClients.TestAsync` says in words whether it works. A failure is a `ModelProviderException` with `Temporary` (busy, unreachable: worth a retry and the next model of a slot) or not (a refused key, an unknown model); its message holds neither the key nor the request.

The tests (`DbDataKit.Agents.Providers.Tests`) run the same class of checks against recorded responses in `fixtures/`, hand-written from the providers' documentation and refreshed by hand (`fixtures/README.md`), never by CI.
