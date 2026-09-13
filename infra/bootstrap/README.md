# AWS account foundation

This stack creates the two things Terraform cannot create from inside its own
remote backend: the encrypted/versioned state bucket and GitHub's OIDC trust.
It is an account-level prerequisite, not an environment layer.

Run it once from an AWS SSO/admin session (never a static access key), record its
outputs as GitHub repository variables, and migrate this small state file to the
bucket it created:

```powershell
terraform -chdir=infra/bootstrap init
terraform -chdir=infra/bootstrap apply `
  -var 'github_repository=iVega123/ProjectY' `
  -var 'state_bucket_name=<globally-unique-name>'
terraform -chdir=infra/bootstrap init -migrate-state `
  -backend-config=backend.hcl
```

Copy `backend.hcl.example` to ignored `backend.hcl`; do not commit account IDs.
Set `AWS_TERRAFORM_PLAN_ROLE_ARN`, `AWS_TERRAFORM_APPLY_ROLE_ARN`,
`AWS_TERRAFORM_STATE_BUCKET`, and `AWS_REGION` from the outputs. GitHub then
exchanges its short-lived OIDC token for a role session; no AWS access key is a
repository secret.
