using System.ComponentModel.DataAnnotations;
using AuthGate.Model;
using Microsoft.AspNetCore.Identity;

namespace AuthGate.Validators;

public sealed class RiderUserDataAnnotationValidator : IUserValidator<ApplicationUser>
{
    public Task<IdentityResult> ValidateAsync(
        UserManager<ApplicationUser> manager,
        ApplicationUser user)
    {
        if (user is not RiderUser rider)
        {
            return Task.FromResult(IdentityResult.Success);
        }

        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(rider, new ValidationContext(rider), results, validateAllProperties: true))
        {
            return Task.FromResult(IdentityResult.Success);
        }

        var errors = results.Select(result => new IdentityError
        {
            Code = "InvalidRider",
            Description = result.ErrorMessage ?? "Os dados do entregador são inválidos."
        });

        return Task.FromResult(IdentityResult.Failed(errors.ToArray()));
    }
}
