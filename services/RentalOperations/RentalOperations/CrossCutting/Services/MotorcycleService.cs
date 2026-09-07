using System.Text.Json;
using RentalOperations.CrossCutting.Model;

namespace RentalOperations.CrossCutting.Services
{
    public class MotorcycleService : IMotorcycleService
    {
        private readonly HttpClient _httpClient;
        public MotorcycleService(IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient("moto-hub");
        }

        public async Task<Motorcycle> GetMotorcycleByIdAsync(string licensePlate)
        {
            try
            {
                using var response = await _httpClient.GetAsync($"api/Motorcycles/{licensePlate}");
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"Request failed with status {response.StatusCode}: {errorContent}", null, response.StatusCode);
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<Motorcycle>(responseBody);
            }
            catch (HttpRequestException e)
            {
                throw new Exception($"Unable to obtain motorcycle data: {e.Message}", e);
            }
        }

        public async Task EnsureHistoricalReferencesAsync(IEnumerable<string> licensePlates)
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "api/Motorcycles/historical-references",
                new { LicensePlates = licensePlates });
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Historical motorcycle reconciliation failed with status {response.StatusCode}: {errorContent}");
            }
        }
    }
}
