-- Staging for payments: cents become dollars. An integer times 0.01 stays exact, so no rounding is involved.
SELECT id AS payment_id,
       order_id,
       payment_method,
       amount_cents * 0.01 AS amount
FROM raw.payments
