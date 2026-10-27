-- T-SQL port of seed.sql for SQL Server. Idempotent: drops and recreates the tables.
DROP TABLE IF EXISTS dim_customer;
DROP TABLE IF EXISTS dim_product;
DROP TABLE IF EXISTS dim_date;
DROP TABLE IF EXISTS dim_month;
DROP TABLE IF EXISTS fact_sales;
DROP TABLE IF EXISTS fact_budget;
DROP TABLE IF EXISTS dim_category;
DROP TABLE IF EXISTS fact_returns;

-- Seed data for the reference engine. Totals match the spec's running example:
-- FY27-Q1 revenue: West A 300, West B 200, East A 100, East B 300
-- FY26-Q1 revenue: West A 240, West B 160, East A 150, East B 150
-- FY27-Q1 budget:  West 600, East 500
-- Orders 3 (West A, 80) and 9 (East B, 100) are Cancelled; every other order is Completed.

CREATE TABLE dim_customer (customer_key INTEGER, customer VARCHAR(100), region VARCHAR(100));
INSERT INTO dim_customer VALUES (1, 'C1', 'West'), (2, 'C2', 'West'), (3, 'C3', 'East'), (4, 'C4', 'North');

CREATE TABLE dim_product (product_key INTEGER, product VARCHAR(100), category VARCHAR(100), category_key INTEGER);
INSERT INTO dim_product VALUES (1, 'A', 'Laptops', 1), (2, 'B', 'Monitors', 2);

CREATE TABLE dim_category (category_key INTEGER, category_group VARCHAR(100));
INSERT INTO dim_category VALUES (1, 'Computing'), (2, 'Peripherals');

CREATE TABLE dim_date (date_key INTEGER, fiscal_month VARCHAR(100), fiscal_quarter VARCHAR(100), fiscal_year VARCHAR(100), prior_year_fiscal_month VARCHAR(100), prior_year_fiscal_quarter VARCHAR(100), calendar_date DATE, prior_year_calendar_date DATE);
INSERT INTO dim_date VALUES
  (20250701, '2025-07', 'FY26-Q1', 'FY26', '2024-07', 'FY25-Q1', '2025-07-01', '2024-07-01'),
  (20250801, '2025-08', 'FY26-Q1', 'FY26', '2024-08', 'FY25-Q1', '2025-08-01', '2024-08-01'),
  (20250901, '2025-09', 'FY26-Q1', 'FY26', '2024-09', 'FY25-Q1', '2025-09-01', '2024-09-01'),
  (20260701, '2026-07', 'FY27-Q1', 'FY27', '2025-07', 'FY26-Q1', '2026-07-01', '2025-07-01'),
  (20260801, '2026-08', 'FY27-Q1', 'FY27', '2025-08', 'FY26-Q1', '2026-08-01', '2025-08-01'),
  (20260901, '2026-09', 'FY27-Q1', 'FY27', '2025-09', 'FY26-Q1', '2026-09-01', '2025-09-01'),
  (20250715, '2025-07', 'FY26-Q1', 'FY26', '2024-07', 'FY25-Q1', '2025-07-15', '2024-07-15'),
  (20250815, '2025-08', 'FY26-Q1', 'FY26', '2024-08', 'FY25-Q1', '2025-08-15', '2024-08-15'),
  (20250915, '2025-09', 'FY26-Q1', 'FY26', '2024-09', 'FY25-Q1', '2025-09-15', '2024-09-15'),
  (20260715, '2026-07', 'FY27-Q1', 'FY27', '2025-07', 'FY26-Q1', '2026-07-15', '2025-07-15'),
  (20260815, '2026-08', 'FY27-Q1', 'FY27', '2025-08', 'FY26-Q1', '2026-08-15', '2025-08-15'),
  (20260915, '2026-09', 'FY27-Q1', 'FY27', '2025-09', 'FY26-Q1', '2026-09-15', '2025-09-15');

CREATE TABLE dim_month (fiscal_month VARCHAR(100), fiscal_quarter VARCHAR(100), fiscal_year VARCHAR(100));
INSERT INTO dim_month SELECT DISTINCT fiscal_month, fiscal_quarter, fiscal_year FROM dim_date;

CREATE TABLE fact_sales (order_id INTEGER, customer_key INTEGER, product_key INTEGER, date_key INTEGER, net_amount DECIMAL(19,4), status VARCHAR(100), ship_date_key INTEGER);
INSERT INTO fact_sales VALUES
  -- FY27-Q1
  (1, 1, 1, 20260701, 100, 'Completed', 20260801), (2, 2, 1, 20260801, 120, 'Completed', 20260901), (3, 1, 1, 20260901, 80, 'Cancelled', 20260901),  -- West A 300
  (4, 1, 2, 20260701, 50, 'Completed', 20260801), (5, 2, 2, 20260801, 70, 'Completed', 20260901), (6, 2, 2, 20260901, 80, 'Completed', 20260901),  -- West B 200
  (7, 3, 1, 20260801, 100, 'Completed', 20260901),                                                          -- East A 100
  (8, 3, 2, 20260701, 100, 'Completed', 20260801), (9, 3, 2, 20260801, 100, 'Cancelled', 20260901), (10, 3, 2, 20260901, 100, 'Completed', 20260901),  -- East B 300
  -- FY26-Q1
  (11, 1, 1, 20250701, 240, 'Completed', 20250801), (12, 2, 2, 20250801, 160, 'Completed', 20250901),
  (13, 3, 1, 20250701, 150, 'Completed', 20250801), (14, 3, 2, 20250901, 150, 'Completed', 20250901);

-- When each order was placed, as a UTC instant. Several are within hours of a month boundary in America/Chicago (UTC-5 in summer): order 1 is
-- June 30 there, order 2 is July 31, order 6 is August 31 at 23:59:59, and order 7 is August 1 at exactly 00:00:00.
ALTER TABLE fact_sales ADD ordered_at datetime2;
UPDATE fact_sales SET ordered_at = CASE order_id WHEN 1 THEN CAST('2026-07-01 03:30:00' AS datetime2) WHEN 2 THEN CAST('2026-08-01 02:00:00' AS datetime2) WHEN 3 THEN CAST('2026-09-01 15:00:00' AS datetime2) WHEN 4 THEN CAST('2026-07-01 18:00:00' AS datetime2) WHEN 5 THEN CAST('2026-08-01 12:00:00' AS datetime2) WHEN 6 THEN CAST('2026-09-01 04:59:59' AS datetime2) WHEN 7 THEN CAST('2026-08-01 05:00:00' AS datetime2) WHEN 8 THEN CAST('2026-07-01 12:00:00' AS datetime2) WHEN 9 THEN CAST('2026-08-01 20:00:00' AS datetime2) WHEN 10 THEN CAST('2026-09-01 10:00:00' AS datetime2) WHEN 11 THEN CAST('2025-07-01 03:00:00' AS datetime2) WHEN 12 THEN CAST('2025-08-01 12:00:00' AS datetime2) WHEN 13 THEN CAST('2025-07-01 12:00:00' AS datetime2) WHEN 14 THEN CAST('2025-09-01 12:00:00' AS datetime2) END;

CREATE TABLE fact_budget (region VARCHAR(100), fiscal_month VARCHAR(100), amount DECIMAL(19,4));
INSERT INTO fact_budget VALUES
  ('West', '2026-07', 200), ('West', '2026-08', 200), ('West', '2026-09', 200),
  ('East', '2026-07', 150), ('East', '2026-08', 150), ('East', '2026-09', 200);

-- Returns share the customer, product and date dimensions with sales. FY27-Q1: West A 30, West B 10, East B 50.
CREATE TABLE fact_returns (return_id INTEGER, customer_key INTEGER, product_key INTEGER, date_key INTEGER, return_amount DECIMAL(19,4));
INSERT INTO fact_returns VALUES (1, 1, 1, 20260801, 30), (2, 3, 2, 20260901, 50), (3, 2, 2, 20260701, 10);
