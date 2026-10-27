SELECT orders.ordered_at_fiscal_month,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE orders.ordered_at_fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
