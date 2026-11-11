SELECT customers.region,
       MEASURE(budget.budget_amount) FILTER (WHERE orders.status = 'Completed') AS completed_budget
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
