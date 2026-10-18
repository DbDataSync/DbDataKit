-- What each playlist holds: tracks, hours of music, the number of genres and the three most common ones as one text.
WITH per_genre AS (
  SELECT p.playlist_id, t.genre_name, count(*) AS tracks
  FROM staging.playlist_tracks p
  JOIN marts.dim_track t ON t.track_id = p.track_id
  GROUP BY p.playlist_id, t.genre_name
),
top_genres AS (
  SELECT playlist_id, string_agg(genre_name, ', ' ORDER BY tracks DESC, genre_name) AS common_genres
  FROM (SELECT *, row_number() OVER (PARTITION BY playlist_id ORDER BY tracks DESC, genre_name) AS rn FROM per_genre) x
  WHERE rn <= 3
  GROUP BY playlist_id
)
SELECT p.playlist_id,
       p.playlist_name,
       count(*) AS tracks,
       CAST(sum(t.length_seconds) / 3600.0 AS DECIMAL(10, 2)) AS hours,
       count(DISTINCT t.genre_name) AS genres,
       max(g.common_genres) AS common_genres
FROM staging.playlists p
JOIN staging.playlist_tracks pt ON pt.playlist_id = p.playlist_id
JOIN marts.dim_track t ON t.track_id = pt.track_id
JOIN top_genres g ON g.playlist_id = p.playlist_id
GROUP BY p.playlist_id, p.playlist_name
