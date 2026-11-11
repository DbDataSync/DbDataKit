SELECT orders.ordered_at_fiscal_month,
       MEASURE(orders.net_revenue),
       prior_period(MEASURE(orders.net_revenue), output := 'value', period := {'unit': 'fiscal_year', 'offset': -1}) AS rev_prior
FROM sales_catalog
WHERE orders.ordered_at BETWEEN '2026-07-01' AND '2026-09-30'
GROUP BY ALL
LIMIT 100
