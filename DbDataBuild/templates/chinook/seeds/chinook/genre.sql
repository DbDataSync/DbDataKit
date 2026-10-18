SELECT * FROM (VALUES
  (1, 'Rock'), (2, 'Jazz'), (3, 'Metal'), (4, 'Alternative'), (5, 'Classical'), (6, 'Blues'),
  (7, 'Latin'), (8, 'Reggae'), (9, 'Pop'), (10, 'Soundtrack'), (11, 'Electronica'), (12, 'Folk')
) AS t(genre_id, name)
