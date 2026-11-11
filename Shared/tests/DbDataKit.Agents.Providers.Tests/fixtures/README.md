# Recorded responses

Each folder holds what one kind of provider answers, written by hand from the provider's published API reference (the shapes of a chat completion, a tool call, a refusal, a stream and an error), with invented ids and the model names the docs use. **No file here was recorded from a live call and CI never makes one.**

| Folder | Wire | What is particular |
| --- | --- | --- |
| `openai/` | chat completions | `refusal`, tool call arguments as a JSON string, usage in a last stream chunk (`stream_options.include_usage`) |
| `foundry/` | chat completions | `api-key` header, content filter results beside the choices, a first stream chunk with no choices |
| `ollama/` | chat completions at `/v1` | no key, no usage in a stream, a call carrying an `index`, an error for a model that is not pulled |
| `anthropic/` | Messages | content blocks, `tool_use` with an object input, `stop_reason`, the typed stream events, `overloaded_error` |

## Refreshing by hand

When a provider changes its answer, make one real request with `curl` (a key of your own, a prompt that calls `thing_show`), save the body here with the invented parts left as they are (ids, key fragments), and run `dotnet test` of `DbDataKit.Agents.Providers.Tests`. A stream is saved with `curl -N` and the blank lines kept. The kinds are the folders; add a folder for a new one and a row in `ProviderRecordingTests.Recordings`.

An optional job against a local Ollama model (plan 27a AG04) is not in CI yet.
