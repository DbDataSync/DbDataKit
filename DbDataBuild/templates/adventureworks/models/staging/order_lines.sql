SELECT sales_order_id,
       sales_order_detail_id,
       product_id,
       order_qty,
       unit_price,
       unit_price_discount,
       special_offer_id,
       order_qty * unit_price * (1 - unit_price_discount) AS line_total
FROM sales.sales_order_detail
