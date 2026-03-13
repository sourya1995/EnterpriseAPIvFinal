# Deployment Setup Guide
# Complete this once before your first deploy. After this, every git push is automatic.

# ─────────────────────────────────────────────────────────────────────────────
# PART 1: AWS SETUP
# ─────────────────────────────────────────────────────────────────────────────

# Install AWS CLI
# Download from: https://aws.amazon.com/cli/
# Then configure it:
aws configure
# Enter your AWS Access Key ID, Secret Access Key, region (eu-west-1), output format (json)

# Create the S3 bucket for Terraform state
aws s3 mb s3://enterprise-terraform-state --region eu-west-1
aws s3api put-bucket-versioning \
  --bucket enterprise-terraform-state \
  --versioning-configuration Status=Enabled
aws s3api put-bucket-encryption \
  --bucket enterprise-terraform-state \
  --server-side-encryption-configuration \
  '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"}}]}'

# Create DynamoDB table for Terraform state locking
# This prevents two people running terraform apply at the same time
aws dynamodb create-table \
  --table-name enterprise-terraform-locks \
  --attribute-definitions AttributeName=LockID,AttributeType=S \
  --key-schema AttributeName=LockID,KeyType=HASH \
  --billing-mode PAY_PER_REQUEST \
  --region eu-west-1

# ─────────────────────────────────────────────────────────────────────────────
# PART 2: STORE SECRETS IN AWS SECRETS MANAGER
# These are the actual sensitive values. Never commit these to git.
# ─────────────────────────────────────────────────────────────────────────────

# Store the database connection string for staging
aws secretsmanager create-secret \
  --name enterprise-api/staging/db-connection \
  --secret-string "Data Source=enterprise-staging.db" \
  --region eu-west-1

# Store the database connection string for production
aws secretsmanager create-secret \
  --name enterprise-api/production/db-connection \
  --secret-string "Data Source=enterprise-prod.db" \
  --region eu-west-1

# Store the JWT secret for staging
# IMPORTANT: Generate a cryptographically strong secret — do NOT use the development one
aws secretsmanager create-secret \
  --name enterprise-api/staging/jwt-secret \
  --secret-string "$(openssl rand -base64 64)" \
  --region eu-west-1

# Store the JWT secret for production
aws secretsmanager create-secret \
  --name enterprise-api/production/jwt-secret \
  --secret-string "$(openssl rand -base64 64)" \
  --region eu-west-1

# To update a secret later (e.g. rotating the JWT secret):
aws secretsmanager update-secret \
  --secret-id enterprise-api/production/jwt-secret \
  --secret-string "your-new-secret-value" \
  --region eu-west-1

# ─────────────────────────────────────────────────────────────────────────────
# PART 3: CREATE IAM USER FOR GITHUB ACTIONS
# Follows least-privilege — only the permissions CI/CD actually needs
# ─────────────────────────────────────────────────────────────────────────────

# Create the IAM user
aws iam create-user --user-name github-actions-enterprise-api

# Create the policy with minimum required permissions
aws iam put-user-policy \
  --user-name github-actions-enterprise-api \
  --policy-name enterprise-api-cicd \
  --policy-document '{
    "Version": "2012-10-17",
    "Statement": [
      {
        "Sid": "ECRAccess",
        "Effect": "Allow",
        "Action": [
          "ecr:GetAuthorizationToken",
          "ecr:BatchCheckLayerAvailability",
          "ecr:GetDownloadUrlForLayer",
          "ecr:BatchGetImage",
          "ecr:InitiateLayerUpload",
          "ecr:UploadLayerPart",
          "ecr:CompleteLayerUpload",
          "ecr:PutImage",
          "ecr:DescribeRepositories",
          "ecr:ListImages"
        ],
        "Resource": "*"
      },
      {
        "Sid": "ECSAccess",
        "Effect": "Allow",
        "Action": [
          "ecs:DescribeServices",
          "ecs:DescribeTaskDefinition",
          "ecs:DescribeTasks",
          "ecs:ListTasks",
          "ecs:RegisterTaskDefinition",
          "ecs:UpdateService"
        ],
        "Resource": "*"
      },
      {
        "Sid": "PassRoleForECS",
        "Effect": "Allow",
        "Action": "iam:PassRole",
        "Resource": [
          "arn:aws:iam::*:role/enterprise-api-*-execution-role",
          "arn:aws:iam::*:role/enterprise-api-*-task-role"
        ]
      }
    ]
  }'

# Create access keys for GitHub Actions
aws iam create-access-key --user-name github-actions-enterprise-api
# COPY THE OUTPUT — you will need AccessKeyId and SecretAccessKey in the next step

# ─────────────────────────────────────────────────────────────────────────────
# PART 4: CONFIGURE GITHUB SECRETS
# Go to: GitHub → Your Repository → Settings → Secrets and variables → Actions
# Add each of these as a Repository Secret:
# ─────────────────────────────────────────────────────────────────────────────

# AWS_ACCESS_KEY_ID       — from the access key you just created
# AWS_SECRET_ACCESS_KEY   — from the access key you just created
# SLACK_WEBHOOK_URL       — from Slack: Apps → Incoming Webhooks → Add to Slack
# STAGING_TEST_ADMIN_EMAIL    — a test admin account email for integration tests
# STAGING_TEST_ADMIN_PASSWORD — that account's password

# ─────────────────────────────────────────────────────────────────────────────
# PART 5: CONFIGURE GITHUB ENVIRONMENTS
# Go to: GitHub → Your Repository → Settings → Environments
# ─────────────────────────────────────────────────────────────────────────────

# Create "staging" environment:
#   - No required reviewers (deploys automatically on push to staging branch)
#   - Add environment secret: AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY if
#     you want separate credentials per environment (recommended)

# Create "production" environment:
#   - Required reviewers: add yourself and any other approvers
#   - This means every production deployment needs a human to click Approve
#   - Wait timer: optionally add a 5-minute wait before deployment can proceed

# ─────────────────────────────────────────────────────────────────────────────
# PART 6: PROVISION INFRASTRUCTURE WITH TERRAFORM
# ─────────────────────────────────────────────────────────────────────────────

# Install Terraform from https://www.terraform.io/downloads
# Then run from the infrastructure/terraform directory:

cd infrastructure/terraform

terraform init

# Preview what will be created — always do this before apply
terraform plan -var-file=staging.tfvars

# Create staging infrastructure
terraform apply -var-file=staging.tfvars

# After staging is confirmed working, create production
terraform plan -var-file=production.tfvars
terraform apply -var-file=production.tfvars

# ─────────────────────────────────────────────────────────────────────────────
# PART 7: FIRST DEPLOY
# After all the above is done, trigger the first deployment:
# ─────────────────────────────────────────────────────────────────────────────

# Push to the staging branch to trigger a staging deployment:
git checkout -b staging
git push origin staging

# Watch the deployment at:
# GitHub → Your Repository → Actions

# Once staging looks good, merge to main for production:
git checkout main
git merge staging
git push origin main

# The CD pipeline will:
# 1. Build and push the Docker image to ECR
# 2. Deploy to staging automatically
# 3. Run integration tests against staging
# 4. Pause for manual approval (production environment protection)
# 5. After approval, deploy to production with blue/green rollout
# 6. Run smoke tests
# 7. Notify Slack

# ─────────────────────────────────────────────────────────────────────────────
# PART 8: EMERGENCY ROLLBACK
# If something goes wrong in production:
# ─────────────────────────────────────────────────────────────────────────────

# Option A — GitHub Actions UI (recommended):
# Go to: Actions → Rollback — Emergency Revert → Run workflow
# Select environment: production
# Enter reason: "API returning 500 errors after deploy"
# Leave revision empty to roll back to previous version automatically

# Option B — AWS CLI directly (fastest in a real incident):
aws ecs update-service \
  --cluster enterprise-api-production \
  --service enterprise-api-production \
  --task-definition enterprise-api-production:PREVIOUS_REVISION_NUMBER \
  --force-new-deployment \
  --region eu-west-1
