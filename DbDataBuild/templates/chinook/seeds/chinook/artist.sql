-- 40 made-up bands: an adjective and a noun, so the same row always has the same name.
SELECT i AS artist_id,
       'The ' || ['Velvet','Crimson','Silent','Electric','Paper','Golden','Hollow','Neon'][1 + pick(i, 1, 8)] || ' ' ||
       ['Foxes','Engines','Harbors','Lanterns','Tigers','Orchards','Signals','Rivers','Mirrors','Stones'][1 + pick(i, 2, 10)] AS name
FROM generate_series(1, 40) AS g(i)
