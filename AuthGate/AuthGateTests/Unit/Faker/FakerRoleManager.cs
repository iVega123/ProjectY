using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace AuthGateTests.Unit.Faker
{
    public class FakeRoleManager<TRole> : RoleManager<TRole> where TRole : class
    {
        public FakeRoleManager(IRoleStore<TRole> store,
                               IEnumerable<IRoleValidator<TRole>> roleValidators,
                               ILookupNormalizer keyNormalizer,
                               IdentityErrorDescriber errors,
                               ILogger<RoleManager<TRole>> logger)
            : base(store, roleValidators, keyNormalizer, errors, logger)
        {
        }
    }
}
