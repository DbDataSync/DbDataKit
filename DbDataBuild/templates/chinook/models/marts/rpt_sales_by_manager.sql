-- Revenue per support agent, rolled up to the manager above them: every manager's figure includes the agents below, and the general manager's the whole company.
WITH agent_revenue AS (
  SELECT c.support_rep_id AS employee_id, sum(f.line_amount) AS revenue
  FROM marts.fct_invoice_lines f
  JOIN staging.customers c ON c.customer_id = f.customer_id
  GROUP BY c.support_rep_id
)
SELECT e.employee_id, e.full_name, e.title, e.level,
       CAST(coalesce(own.revenue, 0) AS DECIMAL(18, 2)) AS own_revenue,
       CAST(coalesce((SELECT sum(r.revenue) FROM agent_revenue r JOIN marts.dim_employee b ON b.employee_id = r.employee_id
                      WHERE b.path LIKE e.path || ' > %'), 0) AS DECIMAL(18, 2)) AS team_revenue
FROM marts.dim_employee e
LEFT JOIN agent_revenue own ON own.employee_id = e.employee_id
