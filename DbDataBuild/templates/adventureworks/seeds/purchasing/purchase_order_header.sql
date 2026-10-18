SELECT d.purchase_order_id,
       1 + pick(d.purchase_order_id, 124, 25) AS vendor_id,
       purchase_date_of(d.purchase_order_id) AS order_date,
       CASE WHEN rnd(d.purchase_order_id, 125) < 0.05 THEN NULL ELSE purchase_date_of(d.purchase_order_id) + to_days(10 + pick(d.purchase_order_id, 126, 25)) END AS ship_date,
       CAST(CASE WHEN rnd(d.purchase_order_id, 125) < 0.05 THEN 2 WHEN rnd(d.purchase_order_id, 127) < 0.03 THEN 4 ELSE 4 END AS SMALLINT) AS status,
       d.sub_total,
       CAST(round(d.sub_total * 0.08, 4) AS DECIMAL(19, 4)) AS tax_amt,
       CAST(round(d.sub_total * 0.03, 4) AS DECIMAL(19, 4)) AS freight
FROM (SELECT purchase_order_id, CAST(sum(order_qty * unit_price) AS DECIMAL(19, 4)) AS sub_total FROM purchasing.purchase_order_detail GROUP BY purchase_order_id) d
