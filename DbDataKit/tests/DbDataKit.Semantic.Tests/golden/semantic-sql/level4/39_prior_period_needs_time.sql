SELECT customers.region,
       MEASURE(orders.net_revenue),
       prior_period(MEASURE(orders.net_revenue), output := 'value', period := {'unit': 'fiscal_year', 'offset': -1}) AS x
FROM sales_catalog
GROUP BY ALL
LIMIT 100
