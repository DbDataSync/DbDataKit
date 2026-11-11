SELECT customers.region,
       date.fiscal_month,
       MEASURE(budget.budget_amount)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY CUBE (customers.region, date.fiscal_month)
LIMIT 100
