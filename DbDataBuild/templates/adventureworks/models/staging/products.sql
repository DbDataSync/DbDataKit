SELECT product_id,
       name AS product_name,
       product_number,
       make_flag AS is_made_in_house,
       coalesce(color, 'N/A') AS color,
       size,
       weight,
       standard_cost,
       list_price,
       product_subcategory_id,
       sell_start_date,
       sell_end_date,
       attributes
FROM production.product
