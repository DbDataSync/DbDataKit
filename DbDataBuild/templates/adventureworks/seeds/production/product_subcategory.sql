SELECT * FROM (VALUES
  (1, 1, 'Mountain Bikes'), (2, 1, 'Road Bikes'), (3, 1, 'Touring Bikes'),
  (4, 2, 'Handlebars'), (5, 2, 'Wheels'), (6, 2, 'Brakes'),
  (7, 3, 'Jerseys'), (8, 3, 'Shorts'), (9, 3, 'Gloves'),
  (10, 4, 'Helmets'), (11, 4, 'Lights'), (12, 4, 'Bottles and Cages')
) AS t(product_subcategory_id, product_category_id, name)
