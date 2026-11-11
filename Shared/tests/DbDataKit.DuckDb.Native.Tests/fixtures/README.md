# Fixtures

`duckdb-1.5.6.duckdb` was written by the DuckDB 1.5.6 engine (`DuckDB.NET.Data.Full` 1.5.6), then checkpointed, with:

```sql
CREATE TABLE orders(id INTEGER PRIMARY KEY, customer VARCHAR, amount DECIMAL(10,2), placed DATE, tags VARCHAR[]);
INSERT INTO orders SELECT i, 'customer-' || (i % 7), (i * 3.25)::DECIMAL(10,2), DATE '2026-01-01' + i::INTEGER, ['a', 'b' || i]
  FROM range(1, 101) t(i);
CREATE VIEW big_orders AS SELECT * FROM orders WHERE amount > 200;
```

It is a binary file: do not regenerate it unless a test needs something it lacks, because its purpose is to be a file the 2.0 engine
did not write (`StorageFormatTests`).
