-- Forty employees; the first twelve are the sales people.
SELECT i AS employee_id,
       'adventure-works\' || lower(['alex', 'blake', 'casey', 'drew', 'emery', 'finn', 'gale', 'harper'][1 + pick(i, 131, 8)]) || i AS login_id,
       CASE WHEN i <= 12 THEN 'Sales Representative' ELSE ['Design Engineer', 'Production Technician', 'Buyer', 'Accountant', 'Quality Inspector', 'Marketing Specialist', 'Recruiter'][1 + pick(i, 132, 7)] END AS job_title,
       DATE '1960-01-01' + to_days(pick(i, 133, 14600)) AS birth_date,
       ['M', 'S'][1 + pick(i, 134, 2)] AS marital_status,
       ['M', 'F'][1 + pick(i, 135, 2)] AS gender,
       DATE '2010-01-01' + to_days(pick(i, 136, 4300)) AS hire_date,
       rnd(i, 137) < 0.5 AS salaried_flag,
       CAST(pick(i, 138, 100) AS SMALLINT) AS vacation_hours,
       CAST(pick(i, 139, 60) AS SMALLINT) AS sick_leave_hours,
       rnd(i, 140) < 0.9 AS current_flag
FROM generate_series(1, 40) AS g(i)
