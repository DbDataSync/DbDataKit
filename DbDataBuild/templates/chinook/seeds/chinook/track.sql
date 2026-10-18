-- Ten tracks to an album (a few have no album), a genre that favours rock and pop, and a composer column that is the messy part:
-- empty for some, one name for most, several names joined with ', ' or ' / ' for others.
WITH t AS (
  SELECT i AS track_id,
         CASE WHEN rnd(i, 11) < 0.02 THEN NULL ELSE 1 + (i - 1) // 10 END AS album_id,
         CASE WHEN rnd(i, 12) < 0.70 THEN 1 WHEN rnd(i, 12) < 0.90 THEN 2 WHEN rnd(i, 12) < 0.97 THEN 3 WHEN rnd(i, 12) < 0.99 THEN 4 ELSE 5 END AS media_type_id,
         CASE WHEN rnd(i, 13) < 0.25 THEN 1 WHEN rnd(i, 13) < 0.40 THEN 9 WHEN rnd(i, 13) < 0.99 THEN 1 + pick(i, 14, 12) ELSE NULL END AS genre_id,
         ['Ana Reyes','Ben Okafor','Chloe Martin','Dev Patel','Elena Rossi','Farid Nasser','Grace Lin','Hugo Weber','Iris Novak','Jonas Berg'] AS pool
  FROM generate_series(1, 1200) AS g(i)
)
SELECT track_id,
       ['Burning','Quiet','Broken','Endless','Little','Electric','Paper','Distant','Golden','Hidden'][1 + pick(track_id, 15, 10)] || ' ' ||
       ['Skies','Hearts','Highway','Garden','Machines','Voices','Letters','Dreams','Fire','Rain'][1 + pick(track_id, 16, 10)] AS name,
       album_id,
       media_type_id,
       genre_id,
       CASE WHEN rnd(track_id, 17) < 0.15 THEN NULL
            WHEN rnd(track_id, 17) < 0.65 THEN pool[1 + pick(track_id, 18, 10)]
            WHEN rnd(track_id, 17) < 0.90 THEN pool[1 + pick(track_id, 18, 10)] || ', ' || pool[1 + pick(track_id, 19, 10)]
            ELSE pool[1 + pick(track_id, 18, 10)] || ' / ' || pool[1 + pick(track_id, 19, 10)] || ' / ' || pool[1 + pick(track_id, 20, 10)] END AS composer,
       90000 + pick(track_id, 21, 420000) AS milliseconds,
       (90000 + pick(track_id, 21, 420000)) * 16 + pick(track_id, 22, 5000) AS bytes,
       CAST(CASE WHEN media_type_id = 5 THEN 1.99 ELSE 0.99 END AS DECIMAL(10, 2)) AS unit_price
FROM t
