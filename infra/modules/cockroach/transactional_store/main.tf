resource "cockroach_cluster" "this" {
  name           = var.name
  cloud_provider = "AWS"
  plan           = var.plan

  serverless = var.plan == "STANDARD" ? {
    usage_limits = {
      provisioned_virtual_cpus = var.standard_vcpus
    }
    upgrade_type            = "AUTOMATIC"
    with_empty_ip_allowlist = true
  } : null

  dedicated = var.plan == "ADVANCED" ? {
    num_virtual_cpus           = var.advanced_vcpus_per_node
    private_network_visibility = true
    storage_gib                = var.advanced_storage_gib_per_node
  } : null

  regions = [for index, region in var.regions : {
    name       = region.name
    node_count = var.plan == "ADVANCED" ? region.node_count : null
    primary    = var.plan == "STANDARD" ? index == 0 : null
  }]

  delete_protection = var.delete_protection
  backup_config = {
    enabled           = true
    frequency_minutes = var.backup_frequency_minutes
    retention_days    = var.backup_retention_days
  }
  labels = var.labels
}

resource "random_password" "service" {
  for_each = var.service_principals

  length           = 32
  special          = true
  override_special = "!#$%&*+-?^_"
}

resource "cockroach_sql_user" "service" {
  for_each = var.service_principals

  cluster_id = cockroach_cluster.this.id
  name       = replace(each.key, "-", "_")
  password   = random_password.service[each.key].result
}

resource "cockroach_private_endpoint_services" "this" {
  cluster_id = cockroach_cluster.this.id
}
