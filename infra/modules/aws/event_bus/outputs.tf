locals {
  first_broker = split(",", aws_msk_cluster.this.bootstrap_brokers_sasl_iam)[0]
}

output "connection" {
  description = "Portable Kafka connection contract."
  value = {
    endpoint         = trimsuffix(local.first_broker, ":9098")
    port             = 9098
    secret_reference = null
  }
}

output "event_bus" {
  description = "Logical Kafka metadata without AWS identifiers."
  value = {
    bootstrap_servers = aws_msk_cluster.this.bootstrap_brokers_sasl_iam
    authentication    = "sasl_iam"
    tls               = true
  }
}
