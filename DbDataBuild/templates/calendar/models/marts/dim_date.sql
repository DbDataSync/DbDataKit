-- A fiscal calendar: one row for every day of three years from 2024-01-01 (the series length is the one number to change). The fiscal year starts in the month
-- ${project.fiscal_start_month}; a fiscal year is named for the calendar year it ends in (July 2024 is FY2025). Each level is a label that carries the year, so each level is
-- many-to-one onto the one above it (`FY2025-P01` is in `FY2025-Q1` is in `FY2025`): that is what the generated hierarchy checks test. The prior_year_* columns are the same labels
-- one year earlier, which is how a semantic model compares a period with the same period a year ago.
WITH days AS (
  SELECT CAST(DATE '2024-01-01' + CAST(n AS INTEGER) AS DATE) AS d
  FROM generate_series(0, 1095) AS t(n)
),
base AS (
  SELECT d, CAST(d - INTERVAL 1 YEAR AS DATE) AS pd FROM days
),
fiscal AS (
  SELECT d, pd,
         year(d) + CASE WHEN month(d) >= ${project.fiscal_start_month} AND ${project.fiscal_start_month} > 1 THEN 1 ELSE 0 END AS fy,
         (month(d) - ${project.fiscal_start_month} + 12) % 12 + 1 AS fm,
         year(pd) + CASE WHEN month(pd) >= ${project.fiscal_start_month} AND ${project.fiscal_start_month} > 1 THEN 1 ELSE 0 END AS pfy,
         (month(pd) - ${project.fiscal_start_month} + 12) % 12 + 1 AS pfm
  FROM base
),
quarters AS (
  SELECT *,
         CASE WHEN fm <= 3 THEN 1 WHEN fm <= 6 THEN 2 WHEN fm <= 9 THEN 3 ELSE 4 END AS fq,
         CASE WHEN pfm <= 3 THEN 1 WHEN pfm <= 6 THEN 2 WHEN pfm <= 9 THEN 3 ELSE 4 END AS pfq
  FROM fiscal
)
SELECT d AS calendar_date,
       'FY' || CAST(fy AS VARCHAR) AS fiscal_year,
       'FY' || CAST(fy AS VARCHAR) || '-Q' || CAST(fq AS VARCHAR) AS fiscal_quarter,
       'FY' || CAST(fy AS VARCHAR) || '-P' || CASE WHEN fm < 10 THEN '0' ELSE '' END || CAST(fm AS VARCHAR) AS fiscal_month,
       pd AS prior_year_calendar_date,
       'FY' || CAST(pfy AS VARCHAR) AS prior_year_fiscal_year,
       'FY' || CAST(pfy AS VARCHAR) || '-Q' || CAST(pfq AS VARCHAR) AS prior_year_fiscal_quarter,
       'FY' || CAST(pfy AS VARCHAR) || '-P' || CASE WHEN pfm < 10 THEN '0' ELSE '' END || CAST(pfm AS VARCHAR) AS prior_year_fiscal_month
FROM quarters
