-- Coleções, destaque e ocasião de exemplo da MO — somente em Development (arquivo 05 §5). Requer o seed S0003.

INSERT INTO catalog.collections (id, name, slug, description, card_theme, position, is_active, created_at, updated_at) VALUES
  ('01930000-0000-7000-8006-000000000001', 'Eles', 'eles',
   'Anéis, pulseiras e correntes para eles, em prata e aço.', 'Graphite', 10, true, now(), now()),
  ('01930000-0000-7000-8006-000000000002', 'Prata 925', 'prata-925',
   'Peças em prata 925 maciça, para usar todos os dias.', 'Ivory', 20, true, now(), now());

INSERT INTO catalog.product_collections (collection_id, product_id, added_at) VALUES
  ('01930000-0000-7000-8006-000000000001', '01930000-0000-7000-8003-000000000003', now()),
  ('01930000-0000-7000-8006-000000000001', '01930000-0000-7000-8003-000000000008', now()),
  ('01930000-0000-7000-8006-000000000002', '01930000-0000-7000-8003-000000000003', now()),
  ('01930000-0000-7000-8006-000000000002', '01930000-0000-7000-8003-000000000008', now());

INSERT INTO catalog.showcase_featured (product_id, updated_at) VALUES
  ('01930000-0000-7000-8003-000000000001', now());

INSERT INTO catalog.occasions (id, name, short_text, starts_on, ends_on, is_active, created_at, updated_at) VALUES
  ('01930000-0000-7000-8007-000000000001', 'Para presentear', 'Peças que vão prontas para a caixinha, sem errar o tamanho.',
   current_date, current_date + 30, true, now(), now());

INSERT INTO catalog.occasion_products (occasion_id, product_id, position) VALUES
  ('01930000-0000-7000-8007-000000000001', '01930000-0000-7000-8003-000000000004', 0),
  ('01930000-0000-7000-8007-000000000001', '01930000-0000-7000-8003-000000000007', 1),
  ('01930000-0000-7000-8007-000000000001', '01930000-0000-7000-8003-000000000009', 2),
  ('01930000-0000-7000-8007-000000000001', '01930000-0000-7000-8003-000000000012', 3);
