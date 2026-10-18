-- Staging maps the source's two-letter status to words, and cuts the timestamp into the date and the month key everything downstream joins on.
SELECT order_id,
       customer_id,
       store_id,
       order_ts,
       CAST(order_ts AS DATE) AS order_date,
       year(order_ts) * 100 + month(order_ts) AS order_month,
       CASE status_code WHEN 'OP' THEN 'open' WHEN 'SH' THEN 'shipped' WHEN 'DL' THEN 'delivered' WHEN 'CX' THEN 'cancelled' ELSE 'unknown' END AS status,
       discount_amount,
       shipping_fee,
       coupon_code IS NOT NULL AS used_coupon
FROM shop.orders
