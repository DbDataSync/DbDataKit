-- A budget is set for a region and a category; stores need their own. It is ALLOCATED to the region's shops in proportion to floor area (the website
-- and the call centre have none, so they get nothing). In whole cents, rounded down, the first shop in the ordering takes the remainder, so the shops
-- always add up to the budget. A region with no shop has nowhere to put its budget: those rows are simply absent, and a test makes sure that is noticed.
WITH weights AS (
  SELECT b.budget_month,
         b.region,
         b.category_code,
         CAST(round(b.budget_amount * 100) AS BIGINT) AS budget_cents,
         s.store_id,
         s.floor_area_m2,
         CAST(sum(s.floor_area_m2) OVER (PARTITION BY b.budget_month, b.region, b.category_code) AS BIGINT) AS region_area,
         row_number() OVER (PARTITION BY b.budget_month, b.region, b.category_code ORDER BY s.floor_area_m2 DESC, s.store_id) AS rank_in_region
  FROM staging.budgets b
  JOIN marts.dim_store s ON s.region = b.region AND s.floor_area_m2 IS NOT NULL
),
floors AS (
  SELECT *, CAST(floor(CAST(budget_cents AS DOUBLE) * floor_area_m2 / region_area) AS BIGINT) AS floor_cents FROM weights
)
SELECT budget_month,
       region,
       category_code,
       store_id,
       CAST(floor_cents + CASE WHEN rank_in_region = 1 THEN budget_cents - CAST(sum(floor_cents) OVER (PARTITION BY budget_month, region, category_code) AS BIGINT) ELSE 0 END AS DECIMAL(18, 0)) * 0.01 AS allocated_budget
FROM floors
