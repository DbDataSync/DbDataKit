# DbDataKit.Store

The write path of master data models (plan 19 Z04, plan 15 section 5), on the tables `DbDataKit.DataModel.Storage` creates (SQL Server and PostgreSQL).

`ModelWriter` applies a batch of `RowChange`s (insert, update, delete) in one transaction:

- **Values** are read the way the attribute's type holds them (a number, text, a date, a UUID, a JSON element); one that does not fit is a `type`, `length` or `precision` violation.
- **Rules of a value**: `required`, `pattern`, `min`, `max`, `allowed`, `unique`, and a `reference` that must exist in the entity it points at (inside the model; a reference to another model is not checked here). A `default` fills an insert.
- **Rules across rows**: `sum_equals` and `no_cycle`, checked once after the whole batch on the groups it touched, so a batch can move a total from one row to another. `no_overlap` is about periods and needs `history: effective_dated`; a model that has one on an entity without history cannot be written (`StoreNotSupportedException`), so a rule is never silently unchecked.
- **History** (`history: effective_dated`, app-managed): each version of a row is a row of the table with `_valid_from` and `_valid_to` (null while it holds). An insert starts the first version at `EffectiveFrom` (now when none); an update **from a date** closes the open version at it and starts the next with the version one higher; an update **with no date** is a correction of the open version in place; a delete **ends** the row at a date and removes nothing. `GetAsync` reads the open version, `GetAsOfAsync` the one that held at a moment, `HistoryAsync` every version. A change cannot start at or before the version it changes, and a key cannot begin again before it ended (`effective`). Unique values, references, `sum_equals` and `no_cycle` are judged against the open versions. `no_overlap` (group_by) refuses two different keys of a group that hold for overlapping periods; one that ends when the next begins is fine. Native temporal tables (SQL Server system-versioned) are the other design the plan allows and are not built.
- **Concurrency**: an update or delete names the version it read; a row that changed is a `VersionConflictException` with the current version. The key does not change (`key_immutable`).
- **A delete** that leaves a row pointing at it is `referenced`.
- **Every rule broken is reported** in one `WriteRefusedException` (`RuleViolation`: rule, entity, attribute, key, a sentence), and then nothing is written.
- **Audit**: each change writes a record in the model's `_audit` table in the same transaction: id (UUID v7), time, who, the entity and key, the action, the versions, and the row before and after as JSON.
