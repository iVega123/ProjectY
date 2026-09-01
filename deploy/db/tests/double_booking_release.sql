-- Fecha o aluguel e aluga a mesma placa de novo. TEM que ser aceito.
--
-- Sem este caso, um índice único comum (sem o predicado `WHERE status =
-- 'active'`) passaria no teste de conflito e ninguém notaria — até uma moto
-- devolvida não poder ser alugada nunca mais. É este arquivo que prova que a
-- restrição é parcial, e não só única.
UPDATE rentals
   SET status = 'closed', ends_at = now(), final_cost = 210.00
 WHERE license_plate = 'CI0T35T' AND status = 'active';

INSERT INTO rentals (rider_id, license_plate, starts_at, predicted_ends_at, init_cost)
VALUES ('rider-ci-3', 'CI0T35T', now(), now() + INTERVAL '7 days', 210.00);
