-- Revenue per genre and year, the genre's share of the year, its rank, and the running total of its revenue over the years.
WITH by_genre AS (
  SELECT d.year, t.genre_name, sum(f.line_amount) AS revenue, sum(f.quantity) AS units
  FROM marts.fct_invoice_lines f
  JOIN marts.dim_date d ON d.date_key = f.date_key
  JOIN marts.dim_track t ON t.track_id = f.track_id
  GROUP BY d.year, t.genre_name
)
SELECT year,
       genre_name,
       CAST(revenue AS DECIMAL(18, 2)) AS revenue,
       CAST(units AS BIGINT) AS units,
       CAST(revenue / sum(revenue) OVER (PARTITION BY year) AS DECIMAL(9, 4)) AS share_of_year,
       rank() OVER (PARTITION BY year ORDER BY revenue DESC) AS revenue_rank,
       CAST(sum(revenue) OVER (PARTITION BY genre_name ORDER BY year ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS DECIMAL(18, 2)) AS revenue_to_date
FROM by_genre
