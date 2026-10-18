SELECT i AS customer_id,
       ['Alex', 'Blake', 'Casey', 'Drew', 'Emery', 'Finn', 'Gale', 'Harper', 'Indigo', 'Jules', 'Kai', 'Lane', 'Morgan', 'Nico', 'Oakley', 'Parker'][1 + pick(i, 21, 16)] AS first_name,
       ['Abbott', 'Bishop', 'Carver', 'Dalton', 'Ellison', 'Foster', 'Grant', 'Hale', 'Irving', 'Jensen', 'Keller', 'Lowell', 'Mercer', 'Nolan'][1 + pick(i, 22, 14)] AS last_name,
       'customer' || i || '@adventure.example' AS email,
       CASE WHEN rnd(i, 23) < 0.4 THEN 1 + pick(i, 24, 5) ELSE 6 + pick(i, 24, 5) END AS territory_id,
       CASE WHEN rnd(i, 25) < 0.2 THEN ['Bike Works', 'Trail Supply', 'Spoke and Chain', 'Two Wheels Co', 'Gear Garage'][1 + pick(i, 26, 5)] END AS store_name
FROM generate_series(1, getvariable('scale')) AS g(i)
