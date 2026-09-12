output "connection" {
  description = "Portable CQL connection contract."
  value = {
    endpoint         = "cassandra.${var.region}.amazonaws.com"
    port             = 9142
    secret_reference = null
  }
}

output "keyspace" {
  description = "Logical CQL store metadata."
  value = {
    name = aws_keyspaces_keyspace.this.name
  }
}
