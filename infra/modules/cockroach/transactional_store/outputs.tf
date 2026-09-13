output "connection" {
  description = "Portable PostgreSQL-wire connection contract."
  value = {
    endpoint         = cockroach_cluster.this.regions[0].internal_dns
    port             = 26257
    secret_reference = null
  }
}

output "cluster" {
  description = "Logical managed-cluster metadata without provider IDs."
  value = {
    name    = cockroach_cluster.this.name
    plan    = cockroach_cluster.this.plan
    regions = [for region in cockroach_cluster.this.regions : region.name]
  }
}

output "private_endpoint_services" {
  description = "AWS PrivateLink service names keyed by region."
  value = {
    for region, service in cockroach_private_endpoint_services.this.services_map :
    region => service.name
  }
}

output "private_endpoint_cluster_id" {
  description = "Implementation-private cluster ID used to accept VPC endpoints in the owning state."
  value       = cockroach_cluster.this.id
}

output "service_secrets" {
  description = "SQL principal material persisted by the environment secret provider."
  value = {
    for service, user in cockroach_sql_user.service : service => {
      username = user.name
      password = random_password.service[service].result
    }
  }
  sensitive = true
}

output "service_secret_references" {
  description = "Logical secret names for one database principal per service."
  value = {
    for service in var.service_principals : service => "${var.name}/${service}/sql"
  }
}
