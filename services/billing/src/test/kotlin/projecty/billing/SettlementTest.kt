package projecty.billing

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * As regras de liquidação, agora com testes.
 *
 * Elas não tinham nenhum: em todo o RentalCoreTests não havia uma asserção
 * sobre `AdditionalCostsOrSavings` ou sobre `FinalTotalCost` -- só sobre quem
 * pode chamar o endpoint. Um cálculo de dinheiro sem teste é um cálculo de
 * dinheiro que ninguém conferiu, e foi assim que a inversão da multa abaixo
 * sobreviveu.
 */
class SettlementTest {
    private val day = 86_400_000L
    private val start = 1_767_225_600_000L // 2026-01-01T00:00:00Z

    private fun settle(
        planDays: Int,
        usedDays: Int,
        dailyMinor: Long = 3_000L,
    ) = Settlement.settle(
        agreedMinor = dailyMinor * planDays,
        planDays = planDays,
        startedAtMs = start,
        predictedEndAtMs = start + planDays * day,
        endedAtMs = start + usedDays * day,
    )

    @Test
    fun `devolucao na data prevista nao ajusta nada`() {
        val settled = settle(planDays = 7, usedDays = 7)

        assertEquals(21_000L, settled.agreedMinor)
        assertEquals(0L, settled.adjustmentMinor)
        assertEquals(21_000L, settled.totalMinor)
    }

    /**
     * A regressão que motiva metade deste serviço.
     *
     * A versão anterior cobrava o combinado INTEIRO e subtraía a multa:
     * 21000 - (3 x 3000 x 0,20) = 19200. Devolver antes saía mais barato que o
     * contrato, o que faz da "multa" um desconto. O correto é pagar os dias
     * usados mais a multa sobre os que sobraram: 4x3000 + 3x3000x0,20 = 13800.
     */
    @Test
    fun `devolucao antecipada cobra os dias usados mais multa sobre os que sobraram`() {
        val settled = settle(planDays = 7, usedDays = 4)

        assertEquals(13_800L, settled.totalMinor)
        assertEquals(-7_200L, settled.adjustmentMinor)
        assertEquals(4, settled.daysUsed)
        assertTrue(settled.totalMinor < settled.agreedMinor, "devolver antes deve custar menos que o contrato")
        assertTrue(settled.totalMinor > 4 * 3_000L, "a multa precisa aparecer acima dos dias usados")
    }

    @Test
    fun `o plano de 15 dias multa em 40 por cento`() {
        val settled = settle(planDays = 15, usedDays = 10, dailyMinor = 2_800L)

        // 10 x 2800 + 5 x 2800 x 0,40 = 28000 + 5600
        assertEquals(33_600L, settled.totalMinor)
    }

    @Test
    fun `atraso soma a diaria extra a cada dia inteiro`() {
        val settled = settle(planDays = 7, usedDays = 9)

        assertEquals(21_000L + 2 * Settlement.LATE_DAY_MINOR, settled.totalMinor)
        assertEquals(2 * Settlement.LATE_DAY_MINOR, settled.adjustmentMinor)
    }

    @Test
    fun `a fracao de dia nao e cobrada de nenhum dos lados`() {
        val agreed = 21_000L
        val halfLate = Settlement.settle(agreed, 7, start, start + 7 * day, start + 7 * day + day / 2)
        val halfEarly = Settlement.settle(agreed, 7, start, start + 7 * day, start + 6 * day + day / 2)

        assertEquals(0L, halfLate.adjustmentMinor)
        assertEquals(6, halfEarly.daysUsed)
    }

    /**
     * A diária sai do combinado, e o arredondamento acontece uma vez só.
     *
     * 1000 em 3 dias dá 333,333... de diária. Um dia usado e dois de multa a
     * 20%: 333,333... + 133,333... = 466,666..., que arredonda para 467.
     *
     * Arredondar cada parcela ANTES daria 333 + 133 = 466. A diferença de um
     * centavo é o ponto: ela existe em toda divisão inexata, e cresce com o
     * número de parcelas. Por isso a regra é arredondar uma vez, no fim.
     */
    @Test
    fun `a divisao inexata arredonda uma vez, no total`() {
        val settled = Settlement.settle(1_000L, 3, start, start + 3 * day, start + day)

        assertEquals(467L, settled.totalMinor)
        assertEquals(-533L, settled.adjustmentMinor)
    }

    @Test
    fun `um plano sem dias nao tem diaria para liquidar`() {
        val error = runCatching { Settlement.settle(1_000L, 0, start, start, start) }.exceptionOrNull()

        assertTrue(error is IllegalArgumentException)
    }
}
