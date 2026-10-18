-- The management chain, walked from the general manager down: the level, and the path of names from the top.
WITH RECURSIVE chain AS (
  SELECT employee_id, full_name, title, reports_to, 1 AS level, full_name AS path
  FROM staging.employees
  WHERE reports_to IS NULL
  UNION ALL
  SELECT e.employee_id, e.full_name, e.title, e.reports_to, c.level + 1, c.path || ' > ' || e.full_name
  FROM staging.employees e
  JOIN chain c ON e.reports_to = c.employee_id
)
SELECT employee_id, full_name, title, reports_to, level, path FROM chain
