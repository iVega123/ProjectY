package projecty.billing

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import com.zaxxer.hikari.HikariConfig
import com.zaxxer.hikari.HikariDataSource
import org.slf4j.LoggerFactory
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicBoolean

private val log = LoggerFactory.getLogger("billing")

private fun env(name: String): String = System.getenv(name) ?: error("$name is required")

fun main() {
    val stopping = AtomicBoolean(false)

    val dataSource =
        HikariDataSource(
            HikariConfig().apply {
                jdbcUrl = env("BILLING_DATABASE_URL")
                // Uma conexão por papel: a relay e o consumidor rodam em threads
                // separadas e cada um abre a sua por operação.
                maximumPoolSize = 8
                connectionTimeout = 5_000
                poolName = "billing"
            },
        )

    val bootstrap = env("KAFKA_BOOTSTRAP_SERVERS")
    val invoices = Invoices(dataSource)
    val consumer = RentalClosedConsumer.kafka(bootstrap, invoices, stopping)
    val relay = InvoiceRelay(dataSource, bootstrap, SchemaRegistry(env("SCHEMA_REGISTRY_URL")), stopping)

    val health =
        HttpServer.create(InetSocketAddress(8094), 0).apply {
            createContext("/health/live") { it.reply(200, "live") }
            createContext("/health/ready") { exchange ->
                // Pronto é ter voltado do poll recentemente. Um consumidor que
                // parou de girar é indistinguível de um processo vivo sem esta
                // pergunta -- e é o modo de falha que interessa aqui.
                val idleMs = System.currentTimeMillis() - consumer.lastPollAtMs.get()
                if (idleMs < 30_000) exchange.reply(200, "ready") else exchange.reply(503, "stalled for ${idleMs}ms")
            }
            createContext("/invoices/count") { it.reply(200, consumer.issued.get().toString()) }
            createContext("/invoices/skipped") { it.reply(200, consumer.skipped.get().toString()) }
            start()
        }

    // Uma thread de trabalho que morre precisa derrubar o processo.
    //
    // Sem isto, o consumidor morre, a relay segue viva, o join() do main nunca
    // volta e /health/live continua respondendo 200 -- um processo que parece
    // saudável e não fatura mais nada. halt() e não exit() porque exit()
    // dispararia o gancho de desligamento, que faz join na thread que está
    // morrendo. O código 70 é EX_SOFTWARE.
    val fatal =
        Thread.UncaughtExceptionHandler { thread, error ->
            log.error("{} stopped; halting so the orchestrator restarts billing", thread.name, error)
            Runtime.getRuntime().halt(70)
        }

    val threads =
        listOf(
            Thread(relay, "invoice-relay"),
            Thread(consumer, "rental-closed"),
        ).onEach { it.uncaughtExceptionHandler = fatal }

    Runtime.getRuntime().addShutdownHook(
        Thread {
            log.info("Stopping billing")
            stopping.set(true)
            consumer.wakeup()
            health.stop(1)
            threads.forEach { it.join(10_000) }
            dataSource.close()
        },
    )

    log.info("billing listening on 8094, settling {} into invoices", RentalClosedConsumer.TOPIC)
    threads.forEach { it.start() }
    threads.forEach { it.join() }
}

private fun HttpExchange.reply(
    status: Int,
    body: String,
) = use {
    val bytes = body.toByteArray()
    responseHeaders.add("Content-Type", "text/plain; charset=utf-8")
    sendResponseHeaders(status, bytes.size.toLong())
    responseBody.write(bytes)
}
