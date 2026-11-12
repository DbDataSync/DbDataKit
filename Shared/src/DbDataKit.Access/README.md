# DbDataKit.Access

The access rules model of the suite (plan 22a, GP01; decision QG2, 2026-11-12). **Access rules are data, not a language.** Nothing here evaluates a rule; the serving core does (GP11), with the resolver of GP10.

## The section (`access/1`)

The `access:` section is part of format 2 of the semantic package (a catalog) and of DbDataStore's `model`; it has no number of its own, the host's format number is the only one a definition carries. A file of format 1 with the section is refused naming the file (`DBD-ACC-012`).

```yaml
access:
  max_age: 1h                        # how old a rules table's data may be (default 1h; at most 31d)
  row:
    - dataset: sales                 # a table of the catalog, an entity of the model
      match: all                     # how the groups combine (default all)
      groups:
        - id: regions
          match: any                 # how the include rules combine; exclude rules always remove rows
          rules:
            - id: by_region
              effect: include
              table: region_access   # the rules table: a table of the same catalog and source (a model: schema.table)
              subject: who           # its column that names the person or group
              subject_kind: group    # group (default) or person
              columns: { region: region_code }   # dataset column: rules-table column; several are matched together
              max_age: 30m           # optional, for this table
            - id: not_closed
              effect: exclude
              table: closed_accounts
              subject: who
              columns: { account: account_id }
  columns:
    - { subject: support, dataset: customers, column: email, treatment: mask, mask: hash }
    - { subject: interns, dataset: customers, column: salary, treatment: hide }
```

- A **rules table** is a table a published Build model builds, in the same target as the dataset it filters, so a rule is a join and never a list of values in the definition. Its rows name a person or group, and strict values: no wildcards, no ranges (a range is a calculated bucket column on the dataset).
- An **include** rule holds for a row when the rules table has a row for one of the caller's names (id, name, a group; compared ignoring case) whose mapped columns are all `IS NOT DISTINCT FROM` the row's values. NULL is an ordinary value: it matches only a NULL in the rules table, so an include rule never matches a NULL value without one, and an exclude rule keeps it.
- An **exclude** rule removes the rows it matches. A group's `match` combines its include rules (`all` or `any`); its excludes always subtract. The dataset's `match` combines its groups. A caller with no matching row for an include rule sees nothing.
- A group needs at least one include rule.
- A **column rule** names a person or group, a dataset and a column, and a `treatment`: `hide`, `mask` (`fixed` or `hash`) or `null`.

## What the loader refuses

With the file, line and column: an unknown key or dataset or column (with the near name), a rules table that is not a table of the definition or is in another source, one that a dimension, measure or relationship reads or that is the dataset itself, a dataset on a source no filter reaches (Analysis Services), a group with no include rule, a `columns` mapping that is empty or uses a rules column twice, a dataset column and a rules column of different kinds, a duplicate id or dataset entry, `mask` on a column that is not text, `cache.scope: shared` beside any rule, a bad `max_age`.

`AccessSectionReader.Read(node, file, diagnostics, target)` reads a section from its YAML node; the `IAccessTarget` is what the host says about its datasets (a catalog, a model). `schemas/access.schema.json` is the shape; a test keeps it equal to the loader's keys and choices.
