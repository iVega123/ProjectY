using RentalOperations.CrossCutting.Model;
using System.Text.Json;

namespace RentalOperations.CrossCutting.Services
{
    public class RiderManagerService : IRiderManagerService
    {
        private readonly HttpClient _httpClient;
        public RiderManagerService(IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient("rider-manager");
        }

        public async Task<Rider> GetRiderByIdAsync(string riderId)
        {
            try
            {
                var response = await _httpClient.GetAsync($"api/Riders/{riderId}");
                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<Rider>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (HttpRequestException e)
            {
                throw new Exception("Failed to retrieve rider data.", e);
            }
        }
    }
}
