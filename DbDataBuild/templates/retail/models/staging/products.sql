SELECT product_id,
       sku,
       product_name,
       category_code,
       unit_price,
       unit_cost,
       weight_grams,
       weight_grams * 0.001 AS weight_kg,
       active
FROM shop.products
