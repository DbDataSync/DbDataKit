SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       prior_period(MEASURE(orders.net_revenue), output := 'value', period := {'unit': 'fiscal_year', 'offset': 1}) AS later
FROM sales_catalog
WHERE date.calendar_date BETWEEN '2025-07-01' AND '2025-08-31'
GROUP BY ALL
LIMIT 100
