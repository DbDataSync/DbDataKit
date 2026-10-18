-- One row per order with what was paid by each method (conditional aggregation turns the payment rows into columns).
SELECT o.order_id,
       o.customer_id,
       o.order_date,
       o.status,
       o.status_group,
       CAST(coalesce(sum(CASE WHEN p.payment_method = 'credit_card' THEN p.amount END), 0) AS DECIMAL(14, 2)) AS credit_card_amount,
       CAST(coalesce(sum(CASE WHEN p.payment_method = 'bank_transfer' THEN p.amount END), 0) AS DECIMAL(14, 2)) AS bank_transfer_amount,
       CAST(coalesce(sum(CASE WHEN p.payment_method = 'coupon' THEN p.amount END), 0) AS DECIMAL(14, 2)) AS coupon_amount,
       CAST(coalesce(sum(CASE WHEN p.payment_method = 'gift_card' THEN p.amount END), 0) AS DECIMAL(14, 2)) AS gift_card_amount,
       CAST(coalesce(sum(p.amount), 0) AS DECIMAL(14, 2)) AS amount
FROM staging.orders o
LEFT JOIN staging.payments p ON p.order_id = o.order_id
GROUP BY ALL
