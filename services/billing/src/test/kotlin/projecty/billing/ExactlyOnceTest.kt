package projecty.billing

import com.zaxxer.hikari.HikariConfig
import com.zaxxer.hikari.HikariDataSource
import org.junit.jupiter.api.AfterAll
import org.junit.jupiter.api.BeforeAll
import org.junit.jupiter.api.Tag
import org.junit.jupiter.api.TestInstance
import org.testcontainers.containers.GenericContainer
import org.testcontainers.utility.DockerImageName
import project_y.events.Invoice.InvoiceIssued
import java.nio.file.Files
import java.nio.file.Path
import java.sql.Connection
import java.sql.DriverManager
import java.sql.SQLException
import java.util.UUID
import javax.sql.DataSource
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * O efeito "exatamente uma vez" do ADR 0009, contra o banco de verdade.
 *
 * Contra o CockroachDB e não contra o Postgres porque a garantia depende do
 * nível de isolamento: SERIALIZABLE é o padrão lá, e é o que faz duas réplicas
 * competindo pela mesma mensagem terminarem com uma nota, não duas. Em READ
 * COMMITTED este teste passaria por sorte.
 */
@TestInstance(TestInstance.Lifecycle.PER_CLASS)
class ExactlyOnceTest {
    private var container: GenericContainer<*>? = null
    private lateinit var endpoint: String
    private lateinit var dataSource: HikariDataSource
    private lateinit var invoices: Invoices

    @BeforeAll
    fun start() {
        // Um CockroachDB já de pé, quando houver, e um container quando não.
        //
        // O Testcontainers precisa falar com o daemon, e nem toda máquina de
        // desenvolvimento expõe o socket de dentro de um container -- o Docker
        // Desktop no Windows não expõe. O CI roda pelo caminho de cima; a
        // variável existe para rodar o teste à mão sem ele.
        endpoint = System.getenv("BILLING_TEST_COCKROACH") ?: startCockroach()

        val root = url("defaultdb")
        awaitReady(root)
        applyFile(root, "000_bootstrap.cockroach.sql")
        val app = url("projecty")
        for (file in listOf("001_schema.sql", "002_rental_core.sql", "003_billing.sql")) applyFile(app, file)

        dataSource =
            HikariDataSource(
                HikariConfig().apply {
                    jdbcUrl = app
                    maximumPoolSize = 4
                },
            )
        invoices = Invoices(dataSource)
    }

    private fun startCockroach(): String {
        val started =
            GenericContainer(DockerImageName.parse("cockroachdb/cockroach:v26.3.1"))
                .withCommand("start-single-node", "--insecure")
                .withExposedPorts(26257)
        started.start()
        container = started
        return "${started.host}:${started.getMappedPort(26257)}"
    }

    @AfterAll
    fun stop() {
        if (this::dataSource.isInitialized) dataSource.close()
        container?.stop()
    }

    @Test
    @Tag("ADR-0009#settlement-inbox")
    fun `a mesma mensagem duas vezes emite uma nota so`() {
        val rental = closed()

        val first = invoices.issue(rental.eventId, rental, settle(rental))
        val second = invoices.issue(rental.eventId, rental, settle(rental))

        assertEquals(Outcome.ISSUED, first.outcome)
        assertEquals(Outcome.DUPLICATE_MESSAGE, second.outcome)
        assertEquals(1, invoiceCount(rental.rentalId))
        // E um evento de saída só: publicar duas vezes o mesmo fato seria a
        // mesma duplicata, um passo adiante.
        assertEquals(1, outboxCount(rental.rentalId))
    }

    @Test
    @Tag("ADR-0009#settlement-inbox")
    fun `mensagem nova para aluguel ja faturado nao emite a segunda nota`() {
        val rental = closed()
        invoices.issue(rental.eventId, rental, settle(rental))

        // O inbox guarda mensagens, não aluguéis: com um id novo ele deixa
        // passar. Quem recusa aqui é one_invoice_per_rental -- a razão de a
        // garantia não depender de uma tabela só.
        val replay = invoices.issue("replay:" + UUID.randomUUID(), rental, settle(rental))

        assertEquals(Outcome.ALREADY_SETTLED, replay.outcome)
        assertNull(replay.invoiceId)
        assertEquals(1, invoiceCount(rental.rentalId))
    }

    /**
     * A propriedade que o rental-core não consegue prometer.
     *
     * O SqlInboxProcessor dele reserva a mensagem, roda o handler fora da
     * transação e conclui depois. Aqui a nota e a linha do inbox estão no mesmo
     * COMMIT: se a nota não entra, a mensagem continua por tratar e a próxima
     * entrega tenta de novo. Não existe estado em que o inbox diga "já tratei"
     * e a nota não exista.
     */
    @Test
    @Tag("ADR-0009#settlement-inbox")
    fun `nota recusada pelo banco nao deixa a mensagem marcada como tratada`() {
        val rental = closed()
        val messageId = rental.eventId
        val impossible = settle(rental).copy(planDays = 0) // viola CHECK (plan_days > 0)

        val error = runCatching { invoices.issue(messageId, rental, impossible) }.exceptionOrNull()

        assertTrue(error is SQLException, "a violação do CHECK precisa chegar a quem chamou")
        assertEquals(0, inboxCount(messageId))
        assertEquals(0, invoiceCount(rental.rentalId))
    }

    @Test
    @Tag("ADR-0009#settlement-inbox")
    fun `o evento de saida carrega a nota e e particionado pelo aluguel`() {
        val rental = closed()
        val settled = settle(rental)

        val issued = invoices.issue(rental.eventId, rental, settled)

        val row = outboxRow(rental.rentalId)
        assertNotNull(row)
        assertEquals("invoice.issued", row.topic)
        assertEquals(rental.rentalId, row.key)
        val event = InvoiceIssued.parseFrom(row.payload)
        assertEquals(settled.totalMinor, event.totalMinor)
        assertEquals(issued.invoiceId.toString(), event.invoiceId)
        assertEquals(InvoiceEvents.eventId(rental.rentalId), event.eventId)
    }

    // ------------------------------------------------------------------ apoio

    private fun closed(): ClosedRental {
        val rentalId = UUID.randomUUID().toString()
        val start = 1_767_225_600_000L
        return ClosedRental(
            eventId = "$rentalId:rental.closed:v1",
            rentalId = rentalId,
            riderId = "rider-" + rentalId.take(8),
            currency = "BRL",
            agreedTotalMinor = 21_000L,
            planDays = 7,
            startedAtMs = start,
            predictedEndAtMs = start + 7 * 86_400_000L,
            endedAtMs = start + 4 * 86_400_000L,
            riderName = "Ada Lovelace",
            traceParent = null,
        )
    }

    private fun settle(rental: ClosedRental) =
        Settlement.settle(
            rental.agreedTotalMinor,
            rental.planDays,
            rental.startedAtMs,
            rental.predictedEndAtMs,
            rental.endedAtMs,
        )

    private class OutboxRow(val key: String, val topic: String, val payload: ByteArray)

    private fun outboxRow(rentalId: String): OutboxRow? =
        query { connection ->
            connection.prepareStatement(
                "SELECT aggregate_id, topic, payload FROM outbox WHERE aggregate_type = 'invoice' AND aggregate_id = ?",
            ).use { statement ->
                statement.setString(1, rentalId)
                statement.executeQuery().use { rows ->
                    if (rows.next()) OutboxRow(rows.getString(1), rows.getString(2), rows.getBytes(3)) else null
                }
            }
        }

    private fun invoiceCount(rentalId: String) =
        count("SELECT count(*) FROM invoices WHERE rental_id = ?::UUID", rentalId)

    private fun outboxCount(rentalId: String) =
        count("SELECT count(*) FROM outbox WHERE aggregate_type = 'invoice' AND aggregate_id = ?", rentalId)

    private fun inboxCount(messageId: String) =
        count("SELECT count(*) FROM inbox WHERE message_id = ? AND consumer = ?", messageId, Invoices.CONSUMER)

    private fun count(
        sql: String,
        vararg arguments: String,
    ): Int =
        query { connection ->
            connection.prepareStatement(sql).use { statement ->
                arguments.forEachIndexed { index, value -> statement.setString(index + 1, value) }
                statement.executeQuery().use { rows -> if (rows.next()) rows.getInt(1) else 0 }
            }
        }

    private fun <T> query(body: (Connection) -> T): T = (dataSource as DataSource).connection.use(body)

    private fun url(database: String) = "jdbc:postgresql://$endpoint/$database?sslmode=disable&user=root"

    private fun awaitReady(url: String) {
        var last: Exception? = null
        repeat(60) {
            try {
                DriverManager.getConnection(url).use { connection ->
                    connection.createStatement().use { it.execute("SELECT 1") }
                }
                return
            } catch (error: SQLException) {
                last = error
                Thread.sleep(1_000)
            }
        }
        throw IllegalStateException("CockroachDB did not become ready", last)
    }

    private fun applyFile(
        url: String,
        file: String,
    ) {
        val sql = Files.readString(Path.of("../../deploy/db/sql", file))
        DriverManager.getConnection(url).use { connection ->
            connection.createStatement().use { it.execute(sql) }
        }
    }
}
