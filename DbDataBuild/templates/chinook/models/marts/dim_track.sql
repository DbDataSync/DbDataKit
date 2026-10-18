-- One row per track with its album, artist, genre and media type, the length as mm:ss text and a length band.
SELECT t.track_id,
       t.track_name,
       a.album_title,
       r.artist_name,
       coalesce(g.genre_name, 'Unknown') AS genre_name,
       m.media_type_name,
       t.composer,
       t.length_seconds,
       lpad(CAST(t.length_seconds // 60 AS VARCHAR), 2, '0') || ':' || lpad(CAST(t.length_seconds % 60 AS VARCHAR), 2, '0') AS length_text,
       CASE WHEN t.length_seconds < 180 THEN 'short' WHEN t.length_seconds < 300 THEN 'standard' ELSE 'long' END AS length_band,
       t.size_mb,
       t.unit_price
FROM staging.tracks t
LEFT JOIN staging.albums a ON a.album_id = t.album_id
LEFT JOIN staging.artists r ON r.artist_id = a.artist_id
LEFT JOIN staging.genres g ON g.genre_id = t.genre_id
JOIN staging.media_types m ON m.media_type_id = t.media_type_id
