-- One row per customer: a first name and a last name from short lists, and an email built from them.
WITH c AS (
  SELECT id,
         ['Ava', 'Ben', 'Cleo', 'Dan', 'Eva', 'Finn', 'Gia', 'Hugo', 'Ida', 'Jon', 'Kai', 'Lena', 'Max', 'Nina', 'Omar', 'Pia', 'Quinn', 'Ravi', 'Sara', 'Theo'][1 + pick(id, 1, 20)] AS first_name,
         ['Adams', 'Baker', 'Chen', 'Diaz', 'Evans', 'Fox', 'Gupta', 'Hall', 'Ito', 'Jones', 'Khan', 'Lopez', 'Moore', 'Nash', 'Ortiz', 'Park', 'Quinn', 'Reyes', 'Singh', 'Tran'][1 + pick(id, 2, 20)] AS last_name
  FROM range(1, getvariable('scale') + 1) AS t(id)
)
SELECT id, first_name, last_name, lower(first_name || '.' || last_name || id || '@example.com') AS email
FROM c
