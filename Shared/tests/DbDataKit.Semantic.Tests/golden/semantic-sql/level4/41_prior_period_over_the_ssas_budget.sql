SELECT date.fiscal_quarter,
       MEASURE(budget.budget_amount),
       prior_period(MEASURE(budget.budget_amount), output := 'value', period := {'unit': 'fiscal_year', 'offset': -1}) AS x
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
