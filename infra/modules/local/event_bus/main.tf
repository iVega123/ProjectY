variable "name" {
  type        = string
  description = "Logical event-bus name."
}

variable "network_name" {
  type        = string
  description = "Docker network name."
}

module "engine" {
  source = "../container_engine"

  name         = var.name
  image        = "apache/kafka:4.1.1"
  network_name = var.network_name
  environment = [
    "CLUSTER_ID=MkU3OEVBNTcwNTJENDM2Qk",
    "KAFKA_ADVERTISED_LISTENERS=DOCKER://${var.name}:9092,HOST://host.docker.internal:29092",
    "KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER",
    "KAFKA_CONTROLLER_QUORUM_VOTERS=1@${var.name}:9093",
    "KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS=0",
    "KAFKA_INTER_BROKER_LISTENER_NAME=DOCKER",
    "KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,DOCKER:PLAINTEXT,HOST:PLAINTEXT",
    "KAFKA_LISTENERS=DOCKER://:9092,HOST://:29092,CONTROLLER://:9093",
    "KAFKA_NODE_ID=1",
    "KAFKA_NUM_PARTITIONS=3",
    "KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1",
    "KAFKA_PROCESS_ROLES=broker,controller",
    "KAFKA_TRANSACTION_STATE_LOG_MIN_ISR=1",
    "KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR=1",
  ]
  ports = [
    { internal = 9092, external = 9092 },
    { internal = 29092, external = 29092 },
  ]
  data_path   = "/var/lib/kafka/data"
  healthcheck = ["CMD-SHELL", "/opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --list >/dev/null"]
}
