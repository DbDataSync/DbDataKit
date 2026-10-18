-- Three albums to an artist.
SELECT i AS album_id,
       ['Midnight','Morning','Static','Gravity','Afterglow','Parallel','Wildfire','Low Tide','Echoes','Northbound'][1 + pick(i, 3, 10)] || ' ' ||
       ['Sessions','Letters','Maps','Variations','Anthems','Stories'][1 + pick(i, 4, 6)] AS title,
       1 + (i - 1) % 40 AS artist_id
FROM generate_series(1, 120) AS g(i)
