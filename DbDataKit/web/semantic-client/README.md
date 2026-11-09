# @dbdatakit/semantic-client

A typed client for semantic queries (plan 22a GP32), for apps and for anything else that has a `fetch`. A request is written in catalog terms (measures, attributes, filters, order, a limit; [docs/semantic/query.md](../../../docs/semantic/query.md)), the answer is a typed result. Nothing here knows a host or a framework: `createSemanticClient(fetcher, base)` takes a `fetch`-shaped function that already carries who is asking (an app's adds the shell's token: `appSemanticClient` of `@dbdatakit/view-app`).

| Call | Route (under `base`, default `/api/v1`) | Answer |
| --- | --- | --- |
| `models()` | `GET /semantic/models` | `{models: [{name, title, version}]}` |
| `catalog(model)` | `GET /semantic/models/{model}/catalog` | `{model, measures: [...], attributes: [...]}` |
| `query(model, request)` | `POST /semantic/models/{model}/query` with `{spec_version: "0.3", kind: "query", query}` | `{status, columns: [{name, type}], rows, warnings, request_hash}`; a row is an array in column order or an object |

A request without a limit (1 to 10,000) or without a measure is refused before anything is sent. A failure is a `SemanticError` with `code` (`not_found`, `denied`, `invalid`, `clarify`, `unsupported`, `unavailable`, `bad_response`), the HTTP status and the server's words; `clarify` and `unsupported` are the engine's answers to a request it will not run. An abort is rethrown as it is.

**The routes are the contract the host will serve, and no host serves them yet**: the apps host's data route answers Store models only. The client is built and tested against a fake host (`npm test -w @dbdatakit/semantic-client`); plan 22a GP35 needs the host's route.
