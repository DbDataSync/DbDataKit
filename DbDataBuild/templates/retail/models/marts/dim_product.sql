-- One row per product with its category and department, a price band, and the margin in money and in percent.
SELECT p.product_id,
       p.sku,
       p.product_name,
       p.category_code,
       coalesce(c.category_name, 'Unknown') AS category_name,
       coalesce(c.department, 'Unknown') AS department,
       p.unit_price,
       p.unit_cost,
       p.unit_price - p.unit_cost AS unit_margin,
       CAST(round((p.unit_price - p.unit_cost) / p.unit_price * 100, 1) AS DECIMAL(5, 1)) AS margin_percent,
       CASE WHEN p.unit_price < 50 THEN 'budget' WHEN p.unit_price < 150 THEN 'standard' WHEN p.unit_price < 300 THEN 'premium' ELSE 'luxury' END AS price_band,
       p.weight_kg,
       p.active
FROM staging.products p
LEFT JOIN staging.categories c ON c.category_code = p.category_code
