-- One row per employee: the department they are in today (the history row that has no end), how long they have been with the company at the start of 2025, and the pay rate in force.
WITH current_pay AS (
  SELECT employee_id, rate, pay_frequency,
         row_number() OVER (PARTITION BY employee_id ORDER BY rate_change_date DESC) AS recency
  FROM staging.pay_history
)
SELECT e.employee_id,
       e.user_name,
       e.job_title,
       e.marital_status,
       e.gender,
       e.hire_date,
       date_diff('year', e.hire_date, DATE '2025-01-01') AS tenure_years,
       e.is_salaried,
       d.department_name,
       d.department_group,
       p.rate AS current_pay_rate,
       CASE p.pay_frequency WHEN 1 THEN 'monthly' WHEN 2 THEN 'biweekly' ELSE 'unknown' END AS pay_frequency
FROM staging.employees e
JOIN staging.department_history h ON h.employee_id = e.employee_id AND h.end_date IS NULL
JOIN staging.departments d ON d.department_id = h.department_id
JOIN current_pay p ON p.employee_id = e.employee_id AND p.recency = 1
