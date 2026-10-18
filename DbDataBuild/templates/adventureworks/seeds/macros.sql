-- Helpers every seed shares. A seed is a SELECT that returns the rows of one source; `seed` and `scale` are
-- variables: `dbdatabuild project seed --seed 7 --scale 800` sets them, and the same two numbers always give the same rows.
SET VARIABLE scale = coalesce(getvariable('scale'), 400);       -- how many customers; orders (four to a customer) and their lines follow from them

-- A number in [0, 1) that depends only on the row, a salt (to make different columns independent) and the seed.
CREATE OR REPLACE MACRO rnd(i, salt) AS (hash(i, salt, getvariable('seed')) % 1000000) / 1000000.0;

-- A whole number in [0, n) with the same property.
CREATE OR REPLACE MACRO pick(i, salt, n) AS CAST(floor(rnd(i, salt) * n) AS INTEGER);

-- How many times a product's price has been raised (0 to 2): the product table has the current value, the history tables walk back from it.
CREATE OR REPLACE MACRO price_steps(product_id) AS pick(product_id, 71, 3);

-- The date of an order or a purchase order, by number, so the lines of an order can find it without reading the header.
CREATE OR REPLACE MACRO order_date_of(i) AS DATE '2022-01-03' + to_days(pick(i, 92, 1090));
CREATE OR REPLACE MACRO purchase_date_of(i) AS DATE '2022-01-10' + to_days(pick(i, 111, 1080));
