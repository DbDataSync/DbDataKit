-- One to three pay changes about eighteen months apart, each a raise of 5 to 10 percent.
SELECT e.employee_id,
       e.hire_date + to_days(548 * k) AS rate_change_date,
       CAST(round((15 + 40 * rnd(e.employee_id, 145)) * pow(1.07, k), 4) AS DECIMAL(10, 4)) AS rate,
       CAST(1 + pick(e.employee_id, 146, 2) AS SMALLINT) AS pay_frequency
FROM humanresources.employee e
CROSS JOIN generate_series(0, 2) AS g(k)
WHERE k <= pick(e.employee_id, 147, 3)
