-- A track can name several composers in one text column, separated by ', ' or ' / '. One row per composer, with the position they were listed in.
-- Each step peels the first name off what is left of the list (a recursive query), which every engine can run.
WITH RECURSIVE pieces AS (
  SELECT track_id,
         1 AS position,
         substr(list, 1, strpos(list, ', ') - 1) AS composer,
         substr(list, strpos(list, ', ') + 2) AS rest
  FROM (SELECT track_id, replace(composer, ' / ', ', ') || ', ' AS list FROM staging.tracks WHERE composer IS NOT NULL) AS lists
  UNION ALL
  SELECT track_id,
         position + 1,
         substr(rest, 1, strpos(rest, ', ') - 1),
         substr(rest, strpos(rest, ', ') + 2)
  FROM pieces
  WHERE length(rest) > 0
)
SELECT track_id, position, composer FROM pieces
