terraform {
  backend "s3" {
    bucket  = "insolvia-terraform-state"
    key     = "insolvia/shared/terraform.tfstate"
    region  = "us-east-1"
    encrypt = true
  }
}
