# SAMPLEDATA: sample data for every model, to run transforms, build tests and stand in for real data

## Goal

Every model a person authors can have sample data that looks like its real data and keeps its structure and constraints: the **source** data of a
Build project (the most valuable: it runs the transforms and is what model tests are built from), the entities of a Store model, and the tables of a
sync definition. The same sample data stands in for real data where an agent or a channel must not see it (AGENTTOOLS's permissions model).

## What exists today

- **Build's seeds** (`DbDataBuild/DESIGN.md`, 15.6): a source table can have `seeds/<schema>/<table>.sql`, one deterministic DuckDB query (variables
  `seed` and `scale`, macros `rnd` and `pick` in `seeds/macros.sql`); seeds may read other seeds, in an order found from the tables they name.
  `project seed` runs them into `.dbdatabuild/seed.duckdb`; `connection seed` puts them on a SQL Server or PostgreSQL sandbox.
- **Build's type-driven generator** (`SampleGenerator`, 15.2): fills a source with no seed from its declared columns, deterministic from a seed, with
  the edge cases (NULLs, case, accents, trailing spaces, empty strings, rounding boundaries). `project sample` runs models on seeded or generated data.
- **Build's model tests** run in DuckDB on given rows.
- Nothing for Store models, sync definitions or agents; the seeds and the generator live in DbDataBuild, not in `Shared/`.
- **They move to `Shared/`** (answer from the owner, 2026-11-19: "Builds seeds and generator definitely move to Shared/"): the seed runner, its
  format and macros, and `SampleGenerator` become a shared library (`Shared/src/DbDataKit.*`, adapter first in DbDataBuild), so Store and Sync models
  use the same ones.

## Open designs

### Sample data by query, per model

**What.** A sample data query per Store entity model or per Build model (its sources first), in DuckDB, as Build's seeds are today; later, a query may
read files attached to the model (a CSV or Parquet file kept with the project). **Why:** an author knows what realistic data looks like (a few real
product codes, the shape of an address) better than any generator. Example: a Store model `customer` has a query that makes 1,000 customers from a list
of city names and a pattern for account numbers, deterministic for a given seed. **Open questions:** whether Build's seed format (a file per table, the
`seed` and `scale` variables, the macros) becomes the shared format as it is or changes on the way to `Shared/`; how attached files are kept and versioned with the project; whether a published sample set is a channel's (`dev`, `test`) or the model's.

### A generic sample data builder from structure and hints

**What.** A builder that makes rows matching a structure (columns and types, keys, uniqueness constraints, references between tables) from hints per
column: example values or a value list, a pattern, a range, a distribution, a cardinality (how many distinct values, how many children per parent),
the share of NULLs. **Why:** a model with no query still gets data that respects its keys and references, so transforms and tests run on it. Example:
`orders.customer_id` references `customers`, 1 to 20 orders per customer, `status` from three values with 80% `shipped`. **Open questions:** where the
hints are written (in the model's column definitions, or a sample file beside it); it extends Build's `SampleGenerator` once that is in `Shared/`; how uniqueness across a composite key and references are kept at scale; whether hints can be inferred from a real table's
profile (counts and distincts, never values) read once from a non-production channel.

### From sample source data to unit tests

**What.** A model test built from sample source data: run the transforms on the sample, review the output, keep it as the expected rows. **Why:** it is
what makes sample source data more valuable than sample output. **Open questions:** how a kept expected output is refreshed when a transform changes on
purpose; whether this is a Workspace screen (the build project's Designer) and a command, or only a command.

### Sample data in place of real data

**What.** The result treatment AGENTTOOLS's permissions model names: an agent in a channel, through a model provider, gets sample rows instead of real
ones for an operation that reads data. **Open questions:** whether the substitute is drawn from the model's published sample set or generated per call
with the same seed; how a person sees that an answer was sample data.

## Needs from other subjects

- AGENTTOOLS-2: the result stage of the pipeline, where sample data replaces real rows for an agent (its requirement R6).

## Shared areas touched

Build's seeds and `SampleGenerator` (moved to `Shared/`), the Store's and Build's model formats (`docs/formats.json`) if hints
are written there, the Workspace's project Designers.
