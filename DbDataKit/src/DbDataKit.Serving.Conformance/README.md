# DbDataKit.Serving.Conformance

The conformance suite for the core services of the serving layer (plan 19, N03): what any host promises a protocol module, written once and run against every host.

Derive one class per host from `ServingServicesConformanceTests` and build a `ServingTarget` (the services, who can sign in, and a few requests whose answers every host gives the same way). The suite checks:

| Promise | Test |
| --- | --- |
| credentials identify a caller or nobody | `Credentials_identify_a_caller_or_nobody` |
| a session id is a bearer token for the caller who opened it, until it is closed | `A_session_id_is_a_bearer_token…` |
| the catalog shows a queryable table with its columns on request, and nothing the caller could not query | `The_catalog_shows…`, `A_caller_sees_nothing…` |
| a query answers with its rows and says where they came from (canonical text, channel, source) | `A_query_answers_with_its_rows…` |
| a limit ends the answer with `ServingLimitException`, never silently | `A_limit_ends_the_answer…` (skipped for a host with no limits) |
| denied, not found, a classified backend failure and invalid are different exceptions | `What_the_caller_may_not_have…` |
| preparing counts the markers and refuses what running would | `Preparing_a_statement…` |
| a source says what it is and whether it takes writes | `A_source_says_what_it_is…` |
| a write needs a write path, and a refused write is `ServingDeniedException` | `A_write_needs_a_write_path…` |

Run today against the fake host (`DbDataKit.Serving.Abstractions.Tests`, with and without a write path) and against Proxy over DuckDB (`DbDataProxy.Tests`). Two flags on the target record what a host cannot say: the adapter over the small host interface lists no sources and knows no dialect, so it cannot refuse the facts of an unknown source or a statement at prepare time. Proxy holds no row limits, so its limit test is skipped.
