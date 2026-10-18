-- Everyone starts on the day they were hired; three in ten move to another department later, which closes the first row and opens a second.
WITH starts AS (
  SELECT e.employee_id, e.hire_date,
         CASE WHEN e.employee_id <= 12 THEN 2 ELSE 1 + pick(e.employee_id, 141, 8) END AS first_department,
         rnd(e.employee_id, 142) < 0.3 AS moves,
         e.hire_date + to_days(365 + pick(e.employee_id, 143, 1100)) AS moved_on
  FROM humanresources.employee e
)
SELECT employee_id, first_department AS department_id, hire_date AS start_date, CASE WHEN moves THEN moved_on - to_days(1) END AS end_date FROM starts
UNION ALL
SELECT employee_id, 1 + (first_department + pick(employee_id, 144, 7)) % 8 AS department_id, moved_on AS start_date, NULL AS end_date FROM starts WHERE moves
