SELECT date.fiscal_month,
       MEASURE(budget.budget_amount),
       prior_period(MEASURE(budget.budget_amount), output := 'delta', period := {'unit': 'fiscal_year', 'offset': -1}) AS chg
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
