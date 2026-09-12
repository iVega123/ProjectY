output "connection" {
  description = "Portable Kubernetes API connection contract."
  value = {
    endpoint         = split(":", trimprefix(kind_cluster.this.endpoint, "https://"))[0]
    port             = tonumber(split(":", trimprefix(kind_cluster.this.endpoint, "https://"))[1])
    secret_reference = null
  }
}

output "cluster" {
  description = "kind connection material consumed only by the local composition root."
  value = {
    name                   = kind_cluster.this.name
    endpoint               = kind_cluster.this.endpoint
    client_certificate     = kind_cluster.this.client_certificate
    client_key             = kind_cluster.this.client_key
    cluster_ca_certificate = kind_cluster.this.cluster_ca_certificate
    kubeconfig             = kind_cluster.this.kubeconfig
  }
  sensitive = true
}
