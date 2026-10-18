-- Helpers every seed shares. A seed is a SELECT that returns the rows of one source; `seed` and `scale` are
-- variables: `dbdatabuild project seed --seed 7 --scale 60` sets them, and the same two numbers always give the same rows.
SET VARIABLE scale = coalesce(getvariable('scale'), 60);       -- how many customers; invoices and their lines follow from them

-- A number in [0, 1) that depends only on the row, a salt (to make different columns independent) and the seed.
CREATE OR REPLACE MACRO rnd(i, salt) AS (hash(i, salt, getvariable('seed')) % 1000000) / 1000000.0;

-- A whole number in [0, n) with the same property.
CREATE OR REPLACE MACRO pick(i, salt, n) AS CAST(floor(rnd(i, salt) * n) AS INTEGER);
