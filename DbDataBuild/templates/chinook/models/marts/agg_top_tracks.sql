-- The three best-selling tracks of every genre (ties broken by track id), with the genre's total, and a pivot of units by year as columns.
WITH sold AS (
  SELECT t.genre_name, t.track_id, t.track_name,
         sum(f.quantity) AS units,
         sum(CASE WHEN d.year = 2021 THEN f.quantity ELSE 0 END) AS units_2021,
         sum(CASE WHEN d.year = 2022 THEN f.quantity ELSE 0 END) AS units_2022,
         sum(CASE WHEN d.year = 2023 THEN f.quantity ELSE 0 END) AS units_2023,
         sum(CASE WHEN d.year = 2024 THEN f.quantity ELSE 0 END) AS units_2024
  FROM marts.fct_invoice_lines f
  JOIN marts.dim_date d ON d.date_key = f.date_key
  JOIN marts.dim_track t ON t.track_id = f.track_id
  GROUP BY t.genre_name, t.track_id, t.track_name
),
ranked AS (
  SELECT *, row_number() OVER (PARTITION BY genre_name ORDER BY units DESC, track_id) AS genre_rank,
         sum(units) OVER (PARTITION BY genre_name) AS genre_units
  FROM sold
)
SELECT genre_name, genre_rank, track_id, track_name,
       CAST(units AS BIGINT) AS units, CAST(units_2021 AS BIGINT) AS units_2021, CAST(units_2022 AS BIGINT) AS units_2022,
       CAST(units_2023 AS BIGINT) AS units_2023, CAST(units_2024 AS BIGINT) AS units_2024, CAST(genre_units AS BIGINT) AS genre_units
FROM ranked
WHERE genre_rank <= 3
