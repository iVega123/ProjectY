variable "primary_region" {
  description = "Normal traffic region."
  type        = string
  default     = "us-east-1"
}

variable "secondary_region" {
  description = "Warm region used after a regional failure."
  type        = string
  default     = "us-west-2"
}

variable "primary_availability_zones" {
  description = "Three failure domains in the primary region."
  type        = list(string)
  default     = ["us-east-1a", "us-east-1b", "us-east-1c"]
}

variable "secondary_availability_zones" {
  description = "Three failure domains in the secondary region."
  type        = list(string)
  default     = ["us-west-2a", "us-west-2b", "us-west-2c"]
}

variable "tags" {
  description = "Additional tags merged with the profile identity."
  type        = map(string)
  default     = {}
}
