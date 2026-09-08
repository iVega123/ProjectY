package projecty.billing

import java.util.UUID
import javax.sql.DataSource

/** A nota como quem a lê precisa dela. Centavos, como o contrato do evento. */
data class InvoiceView(
    val invoiceId: String,
    val rentalId: String,
    val riderId: String,
    val currency: String,
    val planDays: Int,
    val daysUsed: Int,
    val agreedMinor: Long,
    val adjustmentMinor: Long,
    val totalMinor: Long,
    val reason: String,
    val riderName: String?,
    val issuedAtMs: Long,
)

/**
 * A leitura das notas.
 *
 * O filtro por dono é feito no SQL, e não depois em memória, por dois motivos:
 * a linha alheia não chega a atravessar o processo, e não existe um caminho em
 * que alguém acrescente um retorno antes do filtro.
 *
 * Um id que não pertence a quem pediu vira ausência, não 403 -- a mesma escolha
 * do lote de aluguéis do #138. Recusar diria "esta nota existe, mas não é sua",
 * que é precisamente o que alguém varrendo ids quer ouvir.
 */
class InvoiceReads(private val dataSource: DataSource) {
    fun byRentalIds(
        rentalIds: Collection<UUID>,
        riderId: String,
        isAdmin: Boolean,
    ): List<InvoiceView> {
        if (rentalIds.isEmpty()) return emptyList()
        val owned = if (isAdmin) "" else " AND rider_id = ?"
        return dataSource.connection.use { connection ->
            connection.prepareStatement(
                """
                SELECT id, rental_id, rider_id, currency, plan_days, days_used,
                       agreed_minor, adjustment_minor, total_minor, reason, rider_name, issued_at
                  FROM invoices
                 WHERE rental_id = ANY(?)$owned
                 ORDER BY issued_at DESC
                """.trimIndent(),
            ).use { statement ->
                statement.setArray(1, connection.createArrayOf("uuid", rentalIds.toTypedArray()))
                if (!isAdmin) statement.setString(2, riderId)
                statement.executeQuery().use { rows ->
                    buildList {
                        while (rows.next()) {
                            add(
                                InvoiceView(
                                    invoiceId = rows.getString(1),
                                    rentalId = rows.getString(2),
                                    riderId = rows.getString(3),
                                    currency = rows.getString(4),
                                    planDays = rows.getInt(5),
                                    daysUsed = rows.getInt(6),
                                    agreedMinor = rows.getLong(7),
                                    adjustmentMinor = rows.getLong(8),
                                    totalMinor = rows.getLong(9),
                                    reason = rows.getString(10),
                                    riderName = rows.getString(11),
                                    issuedAtMs = rows.getTimestamp(12).time,
                                ),
                            )
                        }
                    }
                }
            }
        }
    }

    companion object {
        /**
         * O mesmo teto do lote de aluguéis, e pelo mesmo motivo: um lote sem
         * limite é uma consulta arbitrária escrita pelo cliente.
         */
        const val MAX_BATCH_SIZE = 100
    }
}
