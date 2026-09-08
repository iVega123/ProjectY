plugins {
    kotlin("jvm") version "2.1.20"
    id("com.google.protobuf") version "0.9.4"
    id("org.jlleitschuh.gradle.ktlint") version "12.1.2"
    application
}

repositories { mavenCentral() }

kotlin { jvmToolchain(21) }

application { mainClass.set("projecty.billing.MainKt") }

// O contrato é o do repositório, não uma cópia.
//
// O #132 pôs os .proto em contracts/events e o registry os governa a partir
// dali. Gerar as classes desta pasta -- e não de uma cópia sob services/billing
// -- é o que faz uma mudança incompatível de contrato quebrar a compilação
// deste serviço em vez de passar despercebida até o consumo.
sourceSets {
    main {
        proto {
            srcDir("../../contracts/events")
            include("rental.proto", "invoice.proto")
        }
    }
}

// Só o gerador Java, que é o padrão do plugin -- declará-lo explicitamente
// duplica o builtin e o build recusa.
protobuf {
    protoc { artifact = "com.google.protobuf:protoc:4.29.3" }
}

// As classes geradas não são escritas à mão e não têm por que passar pelo
// mesmo pente que o código que é.
ktlint {
    filter { exclude { it.file.path.contains("generated") } }
}

dependencies {
    implementation("org.apache.kafka:kafka-clients:3.9.1")
    implementation("com.google.protobuf:protobuf-java:4.29.3")
    implementation("org.postgresql:postgresql:42.7.5")
    implementation("com.zaxxer:HikariCP:6.2.1")
    implementation("com.fasterxml.jackson.core:jackson-databind:2.18.2")
    implementation("org.slf4j:slf4j-api:2.0.16")
    runtimeOnly("ch.qos.logback:logback-classic:1.5.16")

    testImplementation(kotlin("test"))
    testImplementation("org.junit.jupiter:junit-jupiter:5.11.4")
    testImplementation("org.testcontainers:cockroachdb:1.20.4")
    testImplementation("org.testcontainers:junit-jupiter:1.20.4")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

tasks.test {
    useJUnitPlatform()
    testLogging { events("passed", "skipped", "failed") }
}
