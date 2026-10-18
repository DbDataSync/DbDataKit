-- One shipment for every order that has left the warehouse. The number of packages follows the weight (about 15 kg to a package).
WITH w AS (
  SELECT l.order_id, sum(l.quantity * p.weight_grams) AS grams
  FROM shop.order_lines l JOIN shop.products p ON p.product_id = l.product_id
  GROUP BY l.order_id
),
s AS (
  SELECT o.order_id, CAST(o.order_ts AS DATE) AS order_date, w.grams, greatest(1, CAST(ceil(w.grams / 15000.0) AS INTEGER)) AS packages
  FROM shop.orders o JOIN w ON w.order_id = o.order_id
  WHERE o.status_code IN ('SH', 'DL')
)
SELECT row_number() OVER (ORDER BY order_id) AS shipment_id,
       order_id,
       order_date + CAST(1 + pick(order_id, 41, 4) AS INTEGER) AS shipped_date,
       ['UPS', 'DHL', 'FDX', 'USPS'][1 + pick(order_id, 42, 4)] AS carrier_code,
       packages,
       CAST(grams AS INTEGER) AS total_weight_grams,
       CAST(round(5 + packages * 3.5 + grams / 2000.0, 2) AS DECIMAL(10, 2)) AS freight_cost
FROM s
