# DbDataKit.Store

The write path of master data models (plan 19 Z04, plan 15 section 5), on the tables `DbDataKit.DataModel.Storage` creates (SQL Server and PostgreSQL).

`ModelWriter` applies a batch of `RowChange`s (insert, update, delete) in one transaction:

- **Values** are read the way the attribute's type holds them (a number, text, a date, a UUID, a JSON element); one that does not fit is a `type`, `length` or `precision` violation.
- **Rules of a value**: `required`, `pattern`, `min`, `max`, `allowed`, `unique`, and a `reference` that must exist in the entity it points at (inside the model; a reference to another model is not checked here). A `default` fills an insert.
- **Rules across rows**: `sum_equals` and `no_cycle`, checked once after the whole batch on the groups it touched, so a batch can move a total from one row to another. `no_overlap` belongs to effective-dated history (Z05); a model that has one cannot be written until then (`StoreNotSupportedException`), so a rule is never silently unchecked.
- **Concurrency**: an update or delete names the version it read; a row that changed is a `VersionConflictException` with the current version. The key does not change (`key_immutable`).
- **A delete** that leaves a row pointing at it is `referenced`.
- **Every rule broken is reported** in one `WriteRefusedException` (`RuleViolation`: rule, entity, attribute, key, a sentence), and then nothing is written.
- **Audit**: each change writes a record in the model's `_audit` table in the same transaction: id (UUID v7), time, who, the entity and key, the action, the versions, and the row before and after as JSON.
