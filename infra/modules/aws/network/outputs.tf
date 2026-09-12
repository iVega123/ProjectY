output "network" {
  description = "Provider-neutral network capability consumed by the next layer."
  value = {
    id                 = aws_vpc.this.id
    cidr               = aws_vpc.this.cidr_block
    private_subnet_ids = [for zone in var.availability_zones : aws_subnet.private[zone].id]
    public_subnet_ids  = [for zone in var.availability_zones : aws_subnet.public[zone].id]
    availability_zones = var.availability_zones
  }
}

