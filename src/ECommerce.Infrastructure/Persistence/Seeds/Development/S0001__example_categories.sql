-- Catálogo de exemplo — aplicado somente em Development (arquivo 05 §5).
INSERT INTO catalog.categories (id, name, slug, description, seo_title, seo_description, is_active, created_at, updated_at) VALUES
  ('01920000-0000-7000-8000-000000000001', 'Camisetas', 'camisetas',
   'Camisetas de algodão em várias cores e tamanhos, do básico ao estampado.',
   'Camisetas de Algodão', 'Camisetas básicas e estampadas em algodão, com cores e tamanhos do PP ao XG.',
   true, now(), now()),
  ('01920000-0000-7000-8000-000000000002', 'Calças', 'calcas',
   'Calças jeans, sarja e moletom para o dia a dia.',
   NULL, NULL, true, now(), now()),
  ('01920000-0000-7000-8000-000000000003', 'Moletons', 'moletons',
   'Moletons com e sem capuz, para os dias frios.',
   NULL, NULL, true, now(), now()),
  ('01920000-0000-7000-8000-000000000004', 'Acessórios', 'acessorios',
   'Bonés, meias e outros acessórios.',
   NULL, NULL, true, now(), now()),
  ('01920000-0000-7000-8000-000000000005', 'Coleção Antiga', 'colecao-antiga',
   'Categoria desativada, não deve aparecer na loja.',
   NULL, NULL, false, now(), now());
