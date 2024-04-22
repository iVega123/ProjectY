using AuthGate.Model;

namespace AuthGate.Validators
{
    public static class CnhValidator
    {
        public static (bool, TipoCNH) ParseCNHType(string tipoCNH)
        {
            if (Enum.TryParse<TipoCNH>(tipoCNH, true, out TipoCNH result) && IsValidCNHType(tipoCNH))
            {
                return (true, result);
            }
            return (false, default(TipoCNH));
        }

        private static bool IsValidCNHType(string tipoCNH)
        {
            return Enum.GetValues(typeof(TipoCNH))
                       .Cast<TipoCNH>()
                       .Any(cnh => tipoCNH.Equals(cnh.ToString(), StringComparison.OrdinalIgnoreCase));
        }
    }

}
