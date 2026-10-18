-- Length in whole seconds and size in megabytes, so the marts do not repeat the arithmetic.
SELECT track_id,
       name AS track_name,
       album_id,
       media_type_id,
       genre_id,
       composer,
       milliseconds // 1000 AS length_seconds,
       CAST(round(bytes / 1048576.0, 2) AS DECIMAL(10, 2)) AS size_mb,
       unit_price
FROM chinook.track
