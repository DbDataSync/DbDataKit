SELECT customers.region,
       MEASURE(finance.pct_to_budget)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
