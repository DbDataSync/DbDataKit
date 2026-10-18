SELECT order_id,
       line_no,
       product_id,
       quantity,
       unit_price,
       quantity * unit_price AS line_amount
FROM shop.order_lines
