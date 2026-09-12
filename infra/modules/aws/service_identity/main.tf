resource "aws_iam_role" "service" {
  for_each = var.services

  name = "${var.cluster_name}-${each.key}"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid    = "EksPodIdentityOnly"
      Effect = "Allow"
      Principal = {
        Service = "pods.eks.amazonaws.com"
      }
      Action = [
        "sts:AssumeRole",
        "sts:TagSession",
      ]
      Condition = {
        StringEquals = {
          "aws:RequestTag/kubernetes-namespace"       = var.namespace
          "aws:RequestTag/kubernetes-service-account" = each.key
        }
      }
    }]
  })

  tags = merge(var.tags, {
    Name                          = "${var.cluster_name}-${each.key}"
    "projecty.io/service-account" = each.key
  })
}

resource "aws_iam_role_policy" "service" {
  for_each = {
    for name, service in var.services : name => service
    if length(service.statements) > 0
  }

  name = "least-privilege"
  role = aws_iam_role.service[each.key].id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      for statement in each.value.statements : {
        Sid      = statement.sid
        Effect   = "Allow"
        Action   = statement.actions
        Resource = statement.resources
      }
    ]
  })
}

resource "aws_eks_pod_identity_association" "service" {
  for_each = var.services

  cluster_name    = var.cluster_name
  namespace       = var.namespace
  service_account = each.key
  role_arn        = aws_iam_role.service[each.key].arn
  tags            = var.tags
}
