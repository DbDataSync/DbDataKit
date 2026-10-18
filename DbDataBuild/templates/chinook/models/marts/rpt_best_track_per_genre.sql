-- The track of each genre that sold the most units, the lowest track number winning a tie; QUALIFY keeps the first row of each genre's ranking.
SELECT t.genre_name, t.track_id, t.track_name, CAST(sum(f.quantity) AS BIGINT) AS units
FROM marts.fct_invoice_lines f
JOIN marts.dim_track t ON t.track_id = f.track_id
GROUP BY t.genre_name, t.track_id, t.track_name
QUALIFY row_number() OVER (PARTITION BY t.genre_name ORDER BY sum(f.quantity) DESC, t.track_id) = 1
