SELECT customers.region,
       date.fiscal_quarter,
       prior_period(MEASURE(orders.order_count), output := 'value', period := {'unit': 'fiscal_year', 'offset': -1}) AS x
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY date.fiscal_quarter, ROLLUP (customers.region)
LIMIT 100
