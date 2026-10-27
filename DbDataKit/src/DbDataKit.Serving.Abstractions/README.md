# DbDataKit.Serving.Abstractions

The host interface (plan 19, N01; plan 14 R8; plan 20) that the protocol front ends of the serving layer (`DbDataKit.Serving.FlightSql`, later Postgres wire and XMLA) are written against. A protocol library knows
its wire format and nothing about where the data comes from; a host knows its data and nothing about wire formats.

| Member | What the host answers |
| --- | --- |
| `AuthenticateAsync` | who a caller is, from the credentials the protocol received (a password, a bearer token, a Windows identity), or null |
| `DescribeAsync` | what the caller can query: catalogs, schemas, tables and their Arrow schemas, only those the caller may see |
| `LimitsFor` | the limits the caller is held to (rows, time, batch size) |
| `QueryAsync` | an Arrow result of a query, run as the caller and within `LimitsFor`; a limit reached ends the result with `ServingLimitException`, never silently |
| `Writes` | `null` unless the host accepts writes; a protocol never writes a table itself |

Failures a protocol must answer in its own words are exceptions of `ServingException`: `ServingDeniedException` (not allowed), `ServingNotFoundException`, `ServingLimitException`, `ServingInvalidException` (the request is not valid).

## The serving contract (plan 20, section 10; plan 19, N02a)

`IServingHost` is the small host a test or a single data source implements. The modules of the serving layer are written against the core services, which a host offers through `IServingServices`:
`IServingIdentity`, `IServingSessions` (core sessions; the adapter's state is opaque), `IServingQueries` (a `ServingRequest`: SQL, semantic, semantic SQL or MDX; the answer is Arrow plus `ServingProvenance`),
`IServingCatalog` (`ServingObject`: tables, models, dimensions, hierarchies, measures, entities) and `IServingEntities` (null unless offered). Every call takes a `ServingContext` (caller, channel, preview, session, trace).
An `IServingModule` has a name, start, drain, stop and health. `HostServices` offers an `IServingHost` as these services.
