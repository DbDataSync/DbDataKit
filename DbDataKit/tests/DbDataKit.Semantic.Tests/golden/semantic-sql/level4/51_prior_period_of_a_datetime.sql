SELECT orders.ordered_at_fiscal_month,
       MEASURE(orders.net_revenue),
       prior_period(MEASURE(orders.net_revenue), output := 'value', period := {'unit': 'fiscal_year', 'offset': -1}) AS rev_prior
FROM sales_catalog
WHERE orders.ordered_at_fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
