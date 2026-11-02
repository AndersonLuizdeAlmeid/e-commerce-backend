-- Configuração de frete de exemplo — somente em Development (arquivo 05 §5). Sem nenhuma regra (arquivo 20, D08).
INSERT INTO shipping.settings
    (id, origin_postal_code, handling_days, fallback_pac_price, fallback_sedex_price,
     box_weight_grams, box_height_cm, box_width_cm, box_length_cm, updated_at)
VALUES (1, '01310100', 1, 25.00, 45.00, 100, 4, 12, 16, now());
