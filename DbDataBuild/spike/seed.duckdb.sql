-- Synthetic, deterministic seed data for the spike (edge-case pack: case, trailing/leading spaces, empty vs NULL, non-ASCII, NULL keys, duplicates, zero divisor).
CREATE TABLE t (id INTEGER, a INTEGER, b INTEGER, s VARCHAR, d DATE, ts TIMESTAMP, d1 DATE, d2 DATE, x INTEGER);
INSERT INTO t VALUES
 (1, 7,    2,    'abc',    DATE '2024-01-15', TIMESTAMP '2024-01-15 10:30:45', DATE '2024-01-31', DATE '2024-02-01', 1),
 (2, 5,    0,    'ABC',    DATE '2024-02-29', TIMESTAMP '2024-02-29 23:59:59', DATE '2024-12-31', DATE '2025-01-01', 2),
 (3, NULL, 3,    'abc   ', DATE '2024-03-31', TIMESTAMP '2024-03-31 00:00:00', DATE '2024-03-01', DATE '2024-03-31', 3),
 (4, 1,    NULL, ' abc',   DATE '2023-12-31', TIMESTAMP '2023-12-31 12:00:00', DATE '2024-01-01', DATE '2024-01-01', 4),
 (5, 2,    2,    'héllo',  DATE '2024-06-30', TIMESTAMP '2024-06-30 08:15:30', DATE '2024-06-01', DATE '2024-07-01', 5),
 (6, 7,    1,    NULL,     NULL,              NULL,                           NULL,              NULL,              6),
 (7, 3,    3,    '',       DATE '2024-01-01', TIMESTAMP '2024-01-01 00:00:01', DATE '2024-01-15', DATE '2024-01-14', 7),
 (8, 7,    9,    'Abc',    DATE '2024-01-31', TIMESTAMP '2024-01-31 18:45:00', DATE '2024-02-29', DATE '2024-03-01', 8);
CREATE TABLE u (a INTEGER, b INTEGER);
INSERT INTO u VALUES (7, 100), (5, 200), (2, 300), (NULL, 400), (99, 500);
