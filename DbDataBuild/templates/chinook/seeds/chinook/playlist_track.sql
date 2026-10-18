-- Every playlist takes about 6 percent of the tracks; the first one is everything.
SELECT p.playlist_id, t.track_id
FROM chinook.playlist p
CROSS JOIN chinook.track t
WHERE p.playlist_id = 1 OR rnd(p.playlist_id * 100000 + t.track_id, 23) < 0.06
