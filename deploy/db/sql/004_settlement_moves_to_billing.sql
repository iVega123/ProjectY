-- O #137 tirou a liquidação do rental-core, e estas colunas vão junto.
--
-- Elas nasceram em 002 porque a mesma classe que fechava o aluguel calculava o
-- dinheiro: `RentalService.CalculateFinalCostAsync` gravava o total final, o
-- ajuste e a frase que o explicava. Quem produz esses três agora é o billing, e
-- eles moram em `invoices` (003) -- em centavos, junto do id da nota e do
-- motivo, que é onde uma liquidação pertence.
--
-- Este arquivo não as recria condicionalmente nem as preserva: um aluguel
-- fechado antes daqui perde o número que o rental-core guardava, e o
-- `rental.closed` correspondente é o que o billing precisa para refazê-lo. Vale
-- o mesmo que valeu no #135 -- não existe instalação a preservar, e a virada é
-- `docker compose down -v`.
--
-- 001 e 002 ficam como estão. Eles descrevem o que era verdade quando foram
-- escritos; apagar as colunas de lá faria a história dizer que a liquidação
-- nunca esteve aqui, e ela esteve.

ALTER TABLE rentals DROP COLUMN IF EXISTS final_cost;
ALTER TABLE rentals DROP COLUMN IF EXISTS additional_costs;
ALTER TABLE rentals DROP COLUMN IF EXISTS status_message;
