-- One row per product with its subcategory and category, the margin it sells at today, and a few groupings written as mappings.
SELECT p.product_id,
       p.product_name,
       p.product_number,
       c.category_name,
       s.subcategory_name,
       p.is_made_in_house,
       p.color,
       CASE WHEN p.color IN ('Black', 'Silver') THEN 'neutral' WHEN p.color = 'N/A' THEN 'none' ELSE 'colorful' END AS color_group,
       p.size,
       p.weight,
       p.standard_cost,
       p.list_price,
       CAST(p.list_price - p.standard_cost AS DECIMAL(19, 4)) AS margin,
       CAST(round((p.list_price - p.standard_cost) / p.list_price * 100, 1) AS DECIMAL(5, 1)) AS margin_percent,
       CASE WHEN p.list_price < 50 THEN 'budget' WHEN p.list_price < 500 THEN 'mid' WHEN p.list_price < 1500 THEN 'premium' ELSE 'flagship' END AS price_band,
       p.sell_start_date,
       p.sell_end_date,
       p.sell_end_date IS NOT NULL AS is_discontinued
FROM staging.products p
JOIN staging.product_subcategories s ON s.product_subcategory_id = p.product_subcategory_id
JOIN staging.product_categories c ON c.product_category_id = s.product_category_id
