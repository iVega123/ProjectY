using Moq;
using MotoHub.Data;
using MotoHub.Models;

namespace MotoHubTests.Unit.Extensions
{
    public static class MockExtensions
    {
        public static Motorcycle? FirstOrDefault(this Mock<IApplicationDbContext> context, System.Linq.Expressions.Expression<System.Func<Motorcycle, bool>> predicate)
        {
            var motorcycles = context.Object.Motorcycles;
            if (motorcycles != null)
            {
                return motorcycles.FirstOrDefault(predicate.Compile());
            }
            return null;
        }
    }
}
