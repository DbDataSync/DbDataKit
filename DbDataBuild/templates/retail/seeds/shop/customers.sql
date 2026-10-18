-- `scale` customers with a first and last name (some last names have two words), a country, a signup date and an opt-in.
WITH c AS (
  SELECT id,
         ['Ava', 'Ben', 'Cleo', 'Dan', 'Eva', 'Finn', 'Gia', 'Hugo', 'Ida', 'Jon', 'Kai', 'Lena', 'Max', 'Nina', 'Omar', 'Pia', 'Quinn', 'Ravi', 'Sara', 'Theo'][1 + pick(id, 1, 20)] AS first_name,
         ['Adams', 'Baker', 'Chen', 'Diaz', 'van Dijk', 'Fox', 'Gupta', 'de la Hall', 'Ito', 'Jones', 'Khan', 'Lopez', 'Moore', 'Nash', 'Ortiz', 'Park', 'Quinn', 'Reyes', 'Singh', 'Tran'][1 + pick(id, 2, 20)] AS last_name
  FROM range(1, getvariable('scale') + 1) AS t(id)
)
SELECT id AS customer_id,
       first_name || ' ' || last_name AS full_name,
       lower(first_name || '.' || replace(last_name, ' ', '') || id || '@example.com') AS email,
       ['US', 'US', 'US', 'CA', 'GB', 'DE', 'FR', 'AU', 'JP', 'BR'][1 + pick(id, 3, 10)] AS country_code,
       DATE '2020-01-01' + CAST(pick(id, 4, 1500) AS INTEGER) AS signup_date,
       rnd(id, 5) < 0.6 AS marketing_opt_in
FROM c
