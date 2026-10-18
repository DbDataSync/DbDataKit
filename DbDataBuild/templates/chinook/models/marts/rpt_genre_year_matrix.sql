-- Revenue of every genre with a column for each year, by PIVOT over the aggregate table (the list of years is fixed, so the columns are known when the model is defined).
SELECT genre_name, "2021" AS revenue_2021, "2022" AS revenue_2022, "2023" AS revenue_2023, "2024" AS revenue_2024
FROM (
  PIVOT marts.agg_genre_revenue
  ON year IN (2021, 2022, 2023, 2024)
  USING sum(revenue)
  GROUP BY genre_name
)
