output "auth_user_pool_id" {
  description = "The shared development pool this root brands (insolvia-services' insolvia-dev-users)."
  value       = module.auth.user_pool_id
}
