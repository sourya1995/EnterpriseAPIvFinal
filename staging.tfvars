# staging.tfvars
# Run: terraform apply -var-file=staging.tfvars

environment        = "staging"
aws_region         = "eu-west-1"
task_cpu           = 256
task_memory        = 512
desired_count      = 1
min_capacity       = 1
max_capacity       = 4
vpc_id             = "vpc-REPLACE_WITH_YOUR_VPC_ID"
private_subnet_ids = ["subnet-REPLACE_A", "subnet-REPLACE_B"]
public_subnet_ids  = ["subnet-REPLACE_C", "subnet-REPLACE_D"]

# Your external JWT provider details
# Auth0 example:    https://your-tenant.eu.auth0.com/
# Keycloak example: https://auth.yourcompany.com/realms/enterprise
jwt_issuer   = "https://your-auth-provider.com/"
jwt_audience = "enterprise-api-staging"
