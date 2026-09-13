plugins {
    kotlin("jvm") version "2.4.20"
    id("com.google.protobuf") version "0.10.0"
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
    protoc { artifact = "com.google.protobuf:protoc:4.36.1" }
}

// As classes geradas não são escritas à mão e não têm por que passar pelo
// mesmo pente que o código que é.
ktlint {
    filter { exclude { it.file.path.contains("generated") } }
}

dependencies {
    implementation("org.apache.kafka:kafka-clients:3.9.1")
    implementation("com.google.protobuf:protobuf-java:4.36.1")
    implementation("org.postgresql:postgresql:42.7.13")
    implementation("com.zaxxer:HikariCP:7.1.0")
    implementation("com.fasterxml.jackson.core:jackson-databind:2.22.2")
    implementation("org.slf4j:slf4j-api:2.0.19")
    runtimeOnly("ch.qos.logback:logback-classic:1.6.3")

    testImplementation(kotlin("test"))
    testImplementation("org.junit.jupiter:junit-jupiter:5.11.4")
    testImplementation("org.testcontainers:cockroachdb:1.21.4")
    testImplementation("org.testcontainers:junit-jupiter:1.21.4")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

tasks.test {
    useJUnitPlatform()
    testLogging { events("passed", "skipped", "failed") }
}
