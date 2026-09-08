package projecty.billing

import java.math.BigDecimal
import java.math.MathContext
import java.math.RoundingMode

/**
 * As regras de liquidação, e o único lugar onde elas moram.
 *
 * Antes do #137 elas viviam em `RentalService.CalculateFinalCostAsync`, que
 * calculava o dinheiro e mutava o aluguel -- `EndDate`, `FinalCost`, `Status` --
 * dentro de um método chamado `Calculate`. Dois contextos numa classe só, e
 * nenhum teste sobre os números: a busca por `AdditionalCostsOrSavings` nos
 * testes do rental-core não encontrava uma única asserção de valor.
 */
object Settlement {
    /**
     * Meio centavo arredonda para cima.
     *
     * HALF_UP e não HALF_EVEN porque isto é uma cobrança ao cliente, não uma
     * série estatística: o desempate de HALF_EVEN depende do dígito anterior,
     * e explicar isso a quem recebe a fatura custa mais do que o viés que ele
     * corrige. E o arredondamento acontece UMA vez, sobre o total -- arredondar
     * cada parcela e somar deixaria o erro se acumular parcela a parcela.
     */
    val ROUNDING: RoundingMode = RoundingMode.HALF_UP

    /** Diária extra do atraso: R$ 50,00, em centavos. */
    const val LATE_DAY_MINOR: Long = 5_000L

    /**
     * A diária não é recalculada pela tabela de hoje.
     *
     * O evento carrega `agreed_total_minor` e `plan_days`, e a diária sai da
     * divisão dos dois. É deliberado: quem liquida cobra a tarifa que valia
     * quando o aluguel começou, não a que o risk-pricing publicou desde então.
     * Consultar a tabela atual aqui faria o preço de um contrato mudar depois
     * de assinado.
     */
    private val RATE_CONTEXT = MathContext(20, RoundingMode.HALF_UP)

    /**
     * 20% para o plano de 7 dias, 40% para os demais.
     *
     * O enunciado original só nomeia os planos de 7 e 15 dias. Os planos de 30,
     * 45 e 50 dias existem na tabela de preços e não têm multa declarada; esta
     * função os trata como o plano de 15, que é a extensão conservadora -- a
     * mais cara para quem devolve antes. Se alguém decidir outra coisa, é aqui
     * que a decisão muda, e este comentário é o que diz que ela foi tomada.
     */
    fun penaltyRate(planDays: Int): BigDecimal = if (planDays <= 7) BigDecimal("0.20") else BigDecimal("0.40")

    /**
     * O resultado da liquidação.
     *
     * `adjustment` é derivado, e não somado: `total - agreed`. O schema repete
     * a relação como CHECK, então uma das duas contas teria de estar errada
     * para a linha entrar.
     */
    data class Settled(
        val agreedMinor: Long,
        val adjustmentMinor: Long,
        val totalMinor: Long,
        val planDays: Int,
        val daysUsed: Int,
        val reason: String,
    )

    /**
     * Dias inteiros, truncando.
     *
     * Uma fração de dia não é cobrada -- nem a favor do cliente na devolução
     * antecipada, nem contra ele no atraso. É o comportamento que o `.Days` de
     * TimeSpan já tinha no C#, e mudá-lo junto com a mudança de dono
     * transformaria uma mudança de estrutura numa mudança de preço.
     */
    fun wholeDays(
        fromMs: Long,
        toMs: Long,
    ): Int {
        val days = (toMs - fromMs) / 86_400_000L
        return if (days <= 0L) 0 else days.coerceAtMost(Int.MAX_VALUE.toLong()).toInt()
    }

    fun settle(
        agreedMinor: Long,
        planDays: Int,
        startedAtMs: Long,
        predictedEndAtMs: Long,
        endedAtMs: Long,
    ): Settled {
        require(planDays > 0) { "A rental with no planned days has no daily rate to settle against." }
        require(agreedMinor >= 0) { "The agreed total cannot be negative." }

        val daysUsed = wholeDays(startedAtMs, endedAtMs)
        val daysLate = wholeDays(predictedEndAtMs, endedAtMs)

        if (daysLate > 0) {
            // O atraso soma diárias à ponta do contrato; o combinado continua
            // devido por inteiro, porque os dias planejados foram todos usados.
            val extra = LATE_DAY_MINOR * daysLate
            return Settled(
                agreedMinor = agreedMinor,
                adjustmentMinor = extra,
                totalMinor = agreedMinor + extra,
                planDays = planDays,
                daysUsed = daysUsed,
                reason = "Returned $daysLate day(s) late. Extra days charged at the late daily rate.",
            )
        }

        val daysEarly = planDays - daysUsed
        if (daysEarly <= 0) {
            return Settled(
                agreedMinor,
                0L,
                agreedMinor,
                planDays,
                daysUsed,
                "Returned on the predicted end date. No adjustment.",
            )
        }

        // Devolução antecipada: paga-se o que se usou, mais a multa sobre o que
        // se deixou de usar. A versão anterior cobrava o combinado INTEIRO e
        // depois subtraía a multa como desconto, o que fazia devolver antes
        // sair mais barato do que o contrato -- o oposto de uma multa.
        val dailyRate = BigDecimal.valueOf(agreedMinor).divide(BigDecimal(planDays), RATE_CONTEXT)
        val used = dailyRate.multiply(BigDecimal(daysUsed))
        val penalty = dailyRate.multiply(BigDecimal(daysEarly)).multiply(penaltyRate(planDays))
        val total = used.add(penalty).setScale(0, ROUNDING).longValueExact()

        return Settled(
            agreedMinor = agreedMinor,
            adjustmentMinor = total - agreedMinor,
            totalMinor = total,
            planDays = planDays,
            daysUsed = daysUsed,
            reason =
                "Returned $daysEarly day(s) early. Unused days charged at " +
                    "${penaltyRate(planDays).movePointRight(2).toPlainString()}% penalty.",
        )
    }
}
