package projecty.billing

import project_y.events.Invoice.InvoiceIssued
import project_y.events.Rental.RentalEvent
import java.util.UUID

/**
 * O aluguel fechado, do jeito que o billing precisa dele.
 *
 * Tudo o que a liquidação usa chega no evento: o total combinado, os dias do
 * plano, as três datas e o nome do piloto. Nada aqui pede uma consulta de volta
 * ao rental-core -- que é o ponto do estado carregado do ADR 0015, e o que
 * permite ao billing liquidar com o rental-core fora do ar.
 */
data class ClosedRental(
    val eventId: String,
    val rentalId: String,
    val riderId: String,
    val currency: String,
    val agreedTotalMinor: Long,
    val planDays: Int,
    val startedAtMs: Long,
    val predictedEndAtMs: Long,
    val endedAtMs: Long,
    val riderName: String?,
    val traceParent: String?,
) {
    companion object {
        fun from(
            event: RentalEvent,
            traceParent: String?,
        ): ClosedRental {
            require(event.eventId.isNotBlank()) { "rental.closed without an event id cannot be deduplicated." }
            require(event.rentalId.isNotBlank()) { "rental.closed without a rental id cannot be settled." }
            // Um fechamento sem data de fim não é um fechamento. Recusar aqui,
            // por engano do produtor, é melhor do que faturar um aluguel contra
            // a época Unix -- que seria uma nota com dezenas de milhares de
            // diárias de atraso.
            require(event.endedAtMs > 0L) { "rental.closed without ended_at_ms is not a settlement." }
            require(event.planDays > 0) { "rental.closed without plan_days has no daily rate." }
            return ClosedRental(
                eventId = event.eventId,
                rentalId = event.rentalId,
                riderId = event.riderId,
                currency = event.currency.ifBlank { "BRL" },
                agreedTotalMinor = event.agreedTotalMinor,
                planDays = event.planDays,
                startedAtMs = event.startedAtMs,
                predictedEndAtMs = event.predictedEndAtMs,
                endedAtMs = event.endedAtMs,
                riderName = event.riderName.ifBlank { null },
                traceParent = traceParent,
            )
        }
    }
}

object InvoiceEvents {
    /**
     * O id do evento é derivado do aluguel, não sorteado.
     *
     * Mesmo motivo do `RentalEventEnvelope` do rental-core: a relay republica
     * quando cai entre o ProduceAsync e o UPDATE, e o inbox de quem consumir
     * `invoice.issued` só reconhece a repetição se ela chegar com o mesmo id.
     * Um UUID novo a cada publicação transformaria uma reentrega em um segundo
     * fato.
     */
    fun eventId(rentalId: String): String = "$rentalId:invoice.issued:v1"

    fun issued(
        invoiceId: UUID,
        rental: ClosedRental,
        settled: Settlement.Settled,
        occurredAtMs: Long = System.currentTimeMillis(),
    ): InvoiceIssued {
        val builder =
            InvoiceIssued.newBuilder()
                .setEventId(eventId(rental.rentalId))
                .setInvoiceId(invoiceId.toString())
                .setRentalId(rental.rentalId)
                .setRiderId(rental.riderId)
                .setOccurredAtMs(occurredAtMs)
                .setTotalMinor(settled.totalMinor)
                .setCurrency(rental.currency)
        rental.riderName?.let { builder.riderName = it }
        return builder.build()
    }
}
