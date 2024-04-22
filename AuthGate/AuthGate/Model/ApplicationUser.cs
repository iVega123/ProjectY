using Microsoft.AspNetCore.Identity;

namespace AuthGate.Model
{
    public class ApplicationUser : IdentityUser
    {
        public UserType UserType { get; set; }
    }
}
