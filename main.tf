# ─────────────────────────────────────────────────────────────────────────────
# Terraform — ECS Infrastructure
#
# This creates:
#   - ECR repository (stores Docker images)
#   - ECS Cluster (Fargate — no EC2 instances to manage)
#   - ECS Task Definition (what container to run, CPU/memory, env vars)
#   - ECS Service (how many tasks, rolling updates, health checks)
#   - Application Load Balancer (routes traffic to healthy tasks)
#   - IAM roles (what AWS permissions the task has)
#   - CloudWatch log group (where container logs go)
#   - AWS Secrets Manager (stores JWT secret, DB connection string)
#
# Usage:
#   terraform init
#   terraform plan -var-file=staging.tfvars
#   terraform apply -var-file=staging.tfvars
# ─────────────────────────────────────────────────────────────────────────────

terraform {
  required_version = ">= 1.6.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
  }

  # Remote state — never store Terraform state locally in production
  # State contains sensitive values and must be shared across the team
  backend "s3" {
    bucket         = "enterprise-terraform-state"
    key            = "enterprise-api/terraform.tfstate"
    region         = "eu-west-1"
    encrypt        = true
    dynamodb_table = "enterprise-terraform-locks"
  }
}

provider "aws" {
  region = var.aws_region

  default_tags {
    tags = {
      Project     = "EnterpriseAPI"
      Environment = var.environment
      ManagedBy   = "Terraform"
      Repository  = "enterprise-api"
    }
  }
}

# ─── Variables ────────────────────────────────────────────────────────────────
variable "aws_region"        { default = "eu-west-1" }
variable "environment"       { description = "staging or production" }
variable "app_name"          { default = "enterprise-api" }
variable "container_port"    { default = 8080 }
variable "task_cpu"          { default = 512 }
variable "task_memory"       { default = 1024 }
variable "desired_count"     { default = 2 }
variable "min_capacity"      { default = 2 }
variable "max_capacity"      { default = 10 }
variable "vpc_id"            { description = "VPC ID to deploy into" }
variable "private_subnet_ids" { type = list(string) }
variable "public_subnet_ids"  { type = list(string) }
variable "image_uri"         { description = "Full ECR image URI including tag" }
variable "jwt_issuer"        { description = "External JWT provider issuer URL" }
variable "jwt_audience"      { description = "JWT audience claim" }

# ─── Data Sources ─────────────────────────────────────────────────────────────
data "aws_caller_identity" "current" {}
data "aws_region" "current" {}

# ─── ECR Repository ───────────────────────────────────────────────────────────
resource "aws_ecr_repository" "app" {
  name                 = "${var.app_name}-${var.environment}"
  image_tag_mutability = "IMMUTABLE"  # Prevents overwriting existing tags — critical for auditability

  image_scanning_configuration {
    scan_on_push = true  # Automatically scan every pushed image for CVEs
  }

  encryption_configuration {
    encryption_type = "AES256"
  }
}

# Keep only the 10 most recent images — prevents ECR storage costs from growing forever
resource "aws_ecr_lifecycle_policy" "app" {
  repository = aws_ecr_repository.app.name

  policy = jsonencode({
    rules = [{
      rulePriority = 1
      description  = "Keep last 10 images"
      selection = {
        tagStatus   = "any"
        countType   = "imageCountMoreThan"
        countNumber = 10
      }
      action = { type = "expire" }
    }]
  })
}

# ─── CloudWatch Log Group ─────────────────────────────────────────────────────
resource "aws_cloudwatch_log_group" "app" {
  name              = "/ecs/${var.app_name}-${var.environment}"
  retention_in_days = 30  # Keep logs for 30 days — balance cost vs debugging needs

  # Encrypt logs at rest
  # kms_key_id = aws_kms_key.logs.arn  # Uncomment if you have a KMS key
}

# ─── Secrets Manager ──────────────────────────────────────────────────────────
# Secrets are NOT in the task definition or environment variables
# They are pulled from Secrets Manager at container startup by ECS
# This means secrets are never visible in ECS console or task definition JSON

resource "aws_secretsmanager_secret" "db_connection" {
  name                    = "${var.app_name}/${var.environment}/db-connection"
  recovery_window_in_days = 7
}

resource "aws_secretsmanager_secret" "jwt_secret" {
  name                    = "${var.app_name}/${var.environment}/jwt-secret"
  recovery_window_in_days = 7
}

# ─── IAM — Task Execution Role ────────────────────────────────────────────────
# This role is used BY ECS to pull images from ECR and get secrets from Secrets Manager
# The application itself does NOT use this role

resource "aws_iam_role" "task_execution" {
  name = "${var.app_name}-${var.environment}-execution-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Action    = "sts:AssumeRole"
      Effect    = "Allow"
      Principal = { Service = "ecs-tasks.amazonaws.com" }
    }]
  })
}

resource "aws_iam_role_policy_attachment" "task_execution_policy" {
  role       = aws_iam_role.task_execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

# Allow ECS to read secrets — needed for injecting secrets into container env vars
resource "aws_iam_role_policy" "task_execution_secrets" {
  name = "secrets-access"
  role = aws_iam_role.task_execution.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect = "Allow"
      Action = [
        "secretsmanager:GetSecretValue",
        "secretsmanager:DescribeSecret"
      ]
      Resource = [
        aws_secretsmanager_secret.db_connection.arn,
        aws_secretsmanager_secret.jwt_secret.arn
      ]
    }]
  })
}

# ─── IAM — Task Role ──────────────────────────────────────────────────────────
# This role is assumed BY the application code at runtime
# Give it only the permissions the app actually needs (principle of least privilege)

resource "aws_iam_role" "task" {
  name = "${var.app_name}-${var.environment}-task-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Action    = "sts:AssumeRole"
      Effect    = "Allow"
      Principal = { Service = "ecs-tasks.amazonaws.com" }
    }]
  })
}

# ─── Security Groups ──────────────────────────────────────────────────────────
resource "aws_security_group" "alb" {
  name   = "${var.app_name}-${var.environment}-alb-sg"
  vpc_id = var.vpc_id

  ingress {
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
    description = "HTTPS from internet"
  }

  ingress {
    from_port   = 80
    to_port     = 80
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
    description = "HTTP redirect to HTTPS"
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_security_group" "app" {
  name   = "${var.app_name}-${var.environment}-app-sg"
  vpc_id = var.vpc_id

  # Only accept traffic FROM the ALB — never directly from the internet
  ingress {
    from_port       = var.container_port
    to_port         = var.container_port
    protocol        = "tcp"
    security_groups = [aws_security_group.alb.id]
    description     = "Traffic from ALB only"
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
    description = "Allow outbound for external JWT provider, AWS APIs"
  }
}

# ─── Application Load Balancer ────────────────────────────────────────────────
resource "aws_lb" "app" {
  name               = "${var.app_name}-${var.environment}-alb"
  internal           = false
  load_balancer_type = "application"
  security_groups    = [aws_security_group.alb.id]
  subnets            = var.public_subnet_ids

  # Protect against accidental deletion — comment out for staging
  enable_deletion_protection = var.environment == "production"

  access_logs {
    bucket  = aws_s3_bucket.alb_logs.bucket
    prefix  = "${var.app_name}-${var.environment}"
    enabled = true
  }
}

resource "aws_s3_bucket" "alb_logs" {
  bucket        = "${var.app_name}-${var.environment}-alb-logs-${data.aws_caller_identity.current.account_id}"
  force_destroy = var.environment != "production"
}

resource "aws_lb_target_group" "app" {
  name        = "${var.app_name}-${var.environment}-tg"
  port        = var.container_port
  protocol    = "HTTP"
  vpc_id      = var.vpc_id
  target_type = "ip"  # Required for Fargate

  health_check {
    enabled             = true
    healthy_threshold   = 2
    unhealthy_threshold = 3
    timeout             = 5
    interval            = 30
    path                = "/health"
    matcher             = "200"
  }

  # Deregistration delay — gives in-flight requests time to complete before
  # the old task is terminated during a rolling deployment
  deregistration_delay = 30
}

# Redirect HTTP → HTTPS
resource "aws_lb_listener" "http" {
  load_balancer_arn = aws_lb.app.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type = "redirect"
    redirect {
      port        = "443"
      protocol    = "HTTPS"
      status_code = "HTTP_301"
    }
  }
}

resource "aws_lb_listener" "https" {
  load_balancer_arn = aws_lb.app.arn
  port              = 443
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"  # TLS 1.3 preferred
  certificate_arn   = aws_acm_certificate.app.arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.app.arn
  }
}

# ─── ECS Cluster ─────────────────────────────────────────────────────────────
resource "aws_ecs_cluster" "app" {
  name = "${var.app_name}-${var.environment}"

  setting {
    name  = "containerInsights"
    value = "enabled"  # Enables CloudWatch Container Insights for metrics/tracing
  }
}

resource "aws_ecs_cluster_capacity_providers" "app" {
  cluster_name       = aws_ecs_cluster.app.name
  capacity_providers = ["FARGATE", "FARGATE_SPOT"]

  default_capacity_provider_strategy {
    capacity_provider = "FARGATE"
    weight            = 1
    base              = var.min_capacity  # Always keep N tasks on regular Fargate
  }
}

# ─── ECS Task Definition ──────────────────────────────────────────────────────
resource "aws_ecs_task_definition" "app" {
  family                   = "${var.app_name}-${var.environment}"
  network_mode             = "awsvpc"
  requires_compatibilities = ["FARGATE"]
  cpu                      = var.task_cpu
  memory                   = var.task_memory
  execution_role_arn       = aws_iam_role.task_execution.arn
  task_role_arn            = aws_iam_role.task.arn

  container_definitions = jsonencode([{
    name      = var.app_name
    image     = var.image_uri
    essential = true

    portMappings = [{
      containerPort = var.container_port
      protocol      = "tcp"
    }]

    # Environment variables — non-sensitive config only
    # Sensitive values (secrets) come from Secrets Manager below
    environment = [
      { name = "ASPNETCORE_ENVIRONMENT",    value = title(var.environment) },
      { name = "ASPNETCORE_URLS",           value = "http://+:${var.container_port}" },
      { name = "Kafka__Enabled",            value = "false" },
      { name = "JwtSettings__Issuer",       value = var.jwt_issuer },
      { name = "JwtSettings__Audience",     value = var.jwt_audience },
    ]

    # Secrets are injected as environment variables but pulled from Secrets Manager
    # The actual secret values are NEVER stored in the task definition
    secrets = [
      {
        name      = "ConnectionStrings__DefaultConnection"
        valueFrom = aws_secretsmanager_secret.db_connection.arn
      },
      {
        name      = "JwtSettings__Secret"
        valueFrom = aws_secretsmanager_secret.jwt_secret.arn
      }
    ]

    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.app.name
        awslogs-region        = var.aws_region
        awslogs-stream-prefix = "ecs"
      }
    }

    # Health check mirrors the Dockerfile HEALTHCHECK
    healthCheck = {
      command     = ["CMD-SHELL", "curl -f http://localhost:${var.container_port}/health || exit 1"]
      interval    = 30
      timeout     = 5
      retries     = 3
      startPeriod = 30
    }

    # Resource limits — prevent one task from consuming all container memory
    # Hard limit: task is killed if it exceeds this
    # Soft limit: ECS tries to keep memory at or below this
    ulimits = [{
      name      = "nofile"
      softLimit = 65536
      hardLimit = 65536
    }]
  }])
}

# ─── ECS Service ──────────────────────────────────────────────────────────────
resource "aws_ecs_service" "app" {
  name            = "${var.app_name}-${var.environment}"
  cluster         = aws_ecs_cluster.app.id
  task_definition = aws_ecs_task_definition.app.arn
  desired_count   = var.desired_count
  launch_type     = "FARGATE"

  # Rolling deployment configuration
  # minimum_healthy_percent: never go below 100% capacity during deployment
  # maximum_percent: allow up to 200% (old + new tasks) during deployment
  deployment_minimum_healthy_percent = 100
  deployment_maximum_percent         = 200

  deployment_circuit_breaker {
    enable   = true   # Automatically stop a bad deployment
    rollback = true   # And roll back to the previous task definition
  }

  network_configuration {
    subnets          = var.private_subnet_ids  # Tasks run in private subnets
    security_groups  = [aws_security_group.app.id]
    assign_public_ip = false  # Private subnet + NAT gateway for outbound
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.app.arn
    container_name   = var.app_name
    container_port   = var.container_port
  }

  # Allow Terraform to manage task_definition without fighting the CI/CD pipeline
  lifecycle {
    ignore_changes = [task_definition, desired_count]
  }
}

# ─── Auto Scaling ─────────────────────────────────────────────────────────────
resource "aws_appautoscaling_target" "app" {
  max_capacity       = var.max_capacity
  min_capacity       = var.min_capacity
  resource_id        = "service/${aws_ecs_cluster.app.name}/${aws_ecs_service.app.name}"
  scalable_dimension = "ecs:service:DesiredCount"
  service_namespace  = "ecs"
}

# Scale out when CPU exceeds 70% — add more tasks
resource "aws_appautoscaling_policy" "cpu" {
  name               = "${var.app_name}-${var.environment}-cpu-scaling"
  policy_type        = "TargetTrackingScaling"
  resource_id        = aws_appautoscaling_target.app.resource_id
  scalable_dimension = aws_appautoscaling_target.app.scalable_dimension
  service_namespace  = aws_appautoscaling_target.app.service_namespace

  target_tracking_scaling_policy_configuration {
    predefined_metric_specification {
      predefined_metric_type = "ECSServiceAverageCPUUtilization"
    }
    target_value       = 70.0
    scale_in_cooldown  = 300  # Wait 5 min before scaling in — prevents flapping
    scale_out_cooldown = 60   # Scale out quickly in response to load spikes
  }
}

# ─── Outputs ──────────────────────────────────────────────────────────────────
output "alb_dns_name" {
  value       = aws_lb.app.dns_name
  description = "Point your domain's CNAME to this value"
}

output "ecr_repository_url" {
  value       = aws_ecr_repository.app.repository_url
  description = "Push Docker images to this URL"
}

output "ecs_cluster_name" {
  value = aws_ecs_cluster.app.name
}

output "ecs_service_name" {
  value = aws_ecs_service.app.name
}
