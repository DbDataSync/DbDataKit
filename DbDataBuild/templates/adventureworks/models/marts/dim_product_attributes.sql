-- Values read out of the JSON document each product carries: the frame material and the number of gears (bikes only), and the first tag. A product with no document, or no such key, gives NULL.
SELECT product_id,
       json_extract_string(attributes, '$.frame.material') AS frame_material,
       CAST(json_extract_string(attributes, '$.frame.gears') AS INTEGER) AS gears,
       json_extract_string(attributes, '$.tags[0]') AS first_tag,
       attributes IS NOT NULL AS has_attributes
FROM staging.products
