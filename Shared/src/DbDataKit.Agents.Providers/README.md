# DbDataKit.Agents.Providers

The model provider adapters of the agent runtime (plan 27a AG04, AG05). Two `IChatClient`s over HTTP, written against the documented request and response shapes (no provider package):

- `AnthropicChatClient`: Anthropic's Messages API (`/v1/messages`, `x-api-key`, `anthropic-version`), tool use and streaming.
- `OpenAiCompatibleChatClient`: the chat completions API (`/chat/completions`) of OpenAI, Microsoft Foundry (`api-key` header), Ollama (`/v1`) and local servers, tool calls and streaming.

- `EmbeddingClients` (plan 27a KN05): `IEmbeddingGenerator<string, Embedding<float>>` for a provider whose purpose is `embeddings`: `OllamaEmbeddingGenerator` (Ollama's own `/api/embed`; a `/v1` on the connection's address is taken off) and `OpenAiCompatibleEmbeddingGenerator` (`/embeddings`: OpenAI, Microsoft Foundry, compatible servers). Anthropic has no embeddings API. `EmbeddingClients.TestAsync` embeds a sample and reports the number of dimensions.

`ProviderClients.Create` makes the client for a model provider (a connection of kind `model-provider`, read by `ModelProviderConnection` in `DbDataKit.Agents`, `ModelProviders.cs`) and a key resolved from its secret reference; `ProviderClients.TestAsync` says in words whether it works. A failure is a `ModelProviderException` with `Temporary` (busy, unreachable: worth a retry and the next model of a slot) or not (a refused key, an unknown model); its message holds neither the key nor the request.

The tests (`DbDataKit.Agents.Providers.Tests`) run the same class of checks against recorded responses in `fixtures/`, hand-written from the providers' documentation and refreshed by hand (`fixtures/README.md`), never by CI.
