-- Staging: the source as the rest of the project will see it. Names are made consistent and nothing else changes.
SELECT id AS customer_id,
       first_name,
       last_name,
       email
FROM raw.customers
