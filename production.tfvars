# production.tfvars
# Run: terraform apply -var-file=production.tfvars

environment        = "production"
aws_region         = "eu-west-1"
task_cpu           = 512
task_memory        = 1024
desired_count      = 2        # Always 2 minimum — one per AZ for high availability
min_capacity       = 2
max_capacity       = 10
vpc_id             = "vpc-REPLACE_WITH_YOUR_VPC_ID"
private_subnet_ids = ["subnet-REPLACE_A", "subnet-REPLACE_B"]
public_subnet_ids  = ["subnet-REPLACE_C", "subnet-REPLACE_D"]

jwt_issuer   = "https://your-auth-provider.com/"
jwt_audience = "enterprise-api"
