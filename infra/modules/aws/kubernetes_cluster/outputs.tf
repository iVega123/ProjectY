output "connection" {
  description = "Portable Kubernetes API connection contract."
  value = {
    endpoint         = trimprefix(aws_eks_cluster.this.endpoint, "https://")
    port             = 443
    secret_reference = null
  }
}

output "cluster" {
  description = "Kubernetes metadata needed by the platform layer."
  value = {
    name                     = aws_eks_cluster.this.name
    certificate_authority    = aws_eks_cluster.this.certificate_authority[0].data
    pod_identity_agent_ready = aws_eks_addon.pod_identity.id != ""
  }
  sensitive = true
}
