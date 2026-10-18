SELECT i AS vendor_id,
       ['Allied', 'Bright', 'Crest', 'Delta', 'Eagle', 'Fulton', 'Granite', 'Harbor', 'Ironclad', 'Juniper'][1 + pick(i, 112, 10)] || ' ' ||
       ['Cycles', 'Components', 'Metals', 'Textiles', 'Supply', 'Industries'][1 + pick(i, 113, 6)] AS name,
       CAST(1 + pick(i, 114, 5) AS SMALLINT) AS credit_rating,
       rnd(i, 115) < 0.4 AS preferred_vendor,
       rnd(i, 116) < 0.92 AS active
FROM generate_series(1, 25) AS g(i)
