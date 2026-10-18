-- A shipment of several packages becomes one row per package (rows are SPLIT with a series of whole numbers joined on `package_no <= packages`).
-- The shipment's weight and freight cost are split evenly in whole grams and cents, and the last package takes the remainder, so the packages add up to the shipment.
WITH numbers AS (SELECT n AS package_no FROM generate_series(1, 20) AS g(n)),
shipments AS (
  SELECT shipment_id, order_id, shipped_date, carrier_code, packages, total_weight_grams,
         CAST(round(freight_cost * 100) AS INTEGER) AS freight_cents,
         CAST(floor(CAST(total_weight_grams AS DOUBLE) / packages) AS INTEGER) AS grams_each,
         CAST(floor(CAST(round(freight_cost * 100) AS DOUBLE) / packages) AS INTEGER) AS cents_each
  FROM staging.shipments
)
SELECT s.shipment_id,
       n.package_no,
       s.order_id,
       year(s.shipped_date) * 10000 + month(s.shipped_date) * 100 + day(s.shipped_date) AS shipped_date_key,
       s.carrier_code,
       s.grams_each + CASE WHEN n.package_no = s.packages THEN s.total_weight_grams - s.grams_each * s.packages ELSE 0 END AS weight_grams,
       CAST(s.cents_each + CASE WHEN n.package_no = s.packages THEN s.freight_cents - s.cents_each * s.packages ELSE 0 END AS DECIMAL(12, 0)) * 0.01 AS freight_cost
FROM shipments s
JOIN numbers n ON n.package_no <= s.packages
