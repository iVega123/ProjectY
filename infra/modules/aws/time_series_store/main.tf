resource "aws_keyspaces_keyspace" "this" {
  name = var.name

  tags = merge(var.tags, { Name = var.name })
}
