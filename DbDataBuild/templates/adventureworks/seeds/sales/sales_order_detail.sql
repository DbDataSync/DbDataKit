-- One to six lines to an order. The price is the list price of the day, in the currency of the order (a customer's territory has one): dollars divided by the day's rate.
WITH picked AS (
  SELECT i AS sales_order_id, k,
         1 + pick(i * 10 + k, 94, 150) AS product_id,
         CASE WHEN rnd(i * 10 + k, 95) < 0.9 THEN 1 + pick(i * 10 + k, 96, 4) ELSE 11 + pick(i * 10 + k, 96, 14) END AS order_qty,
         order_date_of(i) AS order_date
  FROM generate_series(1, getvariable('scale') * 4) AS g(i)
  CROSS JOIN generate_series(1, 6) AS h(k)
  WHERE k <= 1 + pick(i, 97, 6)
),
priced AS (
  SELECT p.*, t.currency_code, lp.list_price,
         CASE WHEN p.order_qty >= 15 THEN 3 WHEN p.order_qty >= 11 THEN 2
              WHEN p.order_date BETWEEN DATE '2022-03-01' AND DATE '2022-05-31' AND rnd(p.sales_order_id * 10 + p.k, 98) < 0.3 THEN 5
              WHEN p.order_date BETWEEN DATE '2023-11-15' AND DATE '2023-12-31' AND rnd(p.sales_order_id * 10 + p.k, 98) < 0.3 THEN 6
              ELSE 1 END AS special_offer_id
  FROM picked p
  JOIN sales.customer c ON c.customer_id = 1 + pick(p.sales_order_id, 91, getvariable('scale'))
  JOIN sales.sales_territory t ON t.territory_id = c.territory_id
  JOIN production.product_list_price_history lp ON lp.product_id = p.product_id AND p.order_date >= lp.start_date AND (lp.end_date IS NULL OR p.order_date <= lp.end_date)
)
SELECT p.sales_order_id,
       row_number() OVER (ORDER BY p.sales_order_id, p.k) AS sales_order_detail_id,
       p.product_id,
       CAST(p.order_qty AS SMALLINT) AS order_qty,
       CAST(round(p.list_price / coalesce(r.average_rate, 1), 4) AS DECIMAL(19, 4)) AS unit_price,
       CAST(o.discount_pct AS DECIMAL(10, 4)) AS unit_price_discount,
       p.special_offer_id
FROM priced p
JOIN sales.special_offer o ON o.special_offer_id = p.special_offer_id
ASOF LEFT JOIN sales.currency_rate r ON r.to_currency_code = p.currency_code AND p.order_date >= r.currency_rate_date
