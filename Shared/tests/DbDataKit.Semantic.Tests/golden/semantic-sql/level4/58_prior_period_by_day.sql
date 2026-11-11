SELECT date.calendar_date,
       MEASURE(orders.net_revenue),
       prior_period(MEASURE(orders.net_revenue), period := {'unit': 'fiscal_year', 'offset': -1}, output := 'value') AS rev_prior
FROM sales_catalog
WHERE date.calendar_date BETWEEN '2026-07-01' AND '2026-08-31'
GROUP BY ALL
LIMIT 100
