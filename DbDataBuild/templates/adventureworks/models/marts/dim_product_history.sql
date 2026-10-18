-- A slowly changing dimension: a row for every period in which neither the cost nor the list price of a product changed. The cost and the price each have their own history,
-- so the periods start at every change of either; the end of a period is the day before the next change.
WITH change_points AS (
  SELECT product_id, valid_from AS change_date FROM staging.product_cost_history
  UNION
  SELECT product_id, valid_from AS change_date FROM staging.product_price_history
),
periods AS (
  SELECT product_id,
         change_date AS valid_from,
         lead(change_date) OVER (PARTITION BY product_id ORDER BY change_date) AS next_change
  FROM change_points
)
SELECT p.product_id,
       p.valid_from,
       p.next_change - 1 AS valid_to,
       c.standard_cost,
       l.list_price,
       CAST(l.list_price - c.standard_cost AS DECIMAL(19, 4)) AS margin,
       p.next_change IS NULL AS is_current
FROM periods p
JOIN staging.product_cost_history c ON c.product_id = p.product_id AND p.valid_from >= c.valid_from AND (c.valid_to IS NULL OR p.valid_from <= c.valid_to)
JOIN staging.product_price_history l ON l.product_id = p.product_id AND p.valid_from >= l.valid_from AND (l.valid_to IS NULL OR p.valid_from <= l.valid_to)
