using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace CheapPlayitasApi
{

    public class Program
    {
        private const string ApolloApiBasePath = "https://prod-bookingguide.apollotravelgroup.com";
        private const string SalesUnit = "apollorejserdk";

        public static async Task Main(string[] args)
        {
            ServicePointManager.DefaultConnectionLimit = 30;

            var builder = WebApplication.CreateBuilder(args);
            ConfigureServices(builder);

            var app = builder.Build();
            ConfigureMiddleware(app);

            var httpClient = app.Services.GetRequiredService<HttpClient>();
            var cache = app.Services.GetRequiredService<IMemoryCache>();

            ConfigureEndpoints(app, httpClient, cache);

            await app.RunAsync();
        }

        private static void ConfigureServices(WebApplicationBuilder builder)
        {
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddHttpClient();
            builder.Services.AddMemoryCache();

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policyBuilder =>
                {
                    policyBuilder.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader();
                });
            });
        }

        private static void ConfigureMiddleware(WebApplication app)
        {
            app.UseSwagger();
            app.UseSwaggerUI();
            app.UseCors();
        }

        private static void ConfigureEndpoints(WebApplication app, HttpClient httpClient, IMemoryCache cache)
        {
            app.MapGet("/api/prices", async (HttpContext context) =>
            {
                Console.WriteLine($"Starting request to prices {DateTime.Now}");

                int persons = context.Request.Query.TryGetValue("persons", out var personsValue) ? int.Parse(personsValue) : 2;
                string cacheKey = $"PricesCache{persons}";

                var prices = await cache.GetOrCreateAsync(cacheKey, async entry =>
                {
                    entry.SetAbsoluteExpiration(TimeSpan.FromHours(20));
                    return await GetHotelsAndPricesAsync(httpClient, persons);
                });

                if (prices.Count == 0)
                {
                    cache.Remove(cacheKey);
                }

                Console.WriteLine($"Finishing request to prices {DateTime.Now}");
                return prices;
            });
        }

        private static async Task<List<TravelPrice>> GetHotelsAndPricesAsync(HttpClient httpClient, int persons)
        {
            var airports = new List<string> { "CPH", "BLL", "AAL" };
            var durations = new List<string> { Duration.OneWeek, Duration.TwoWeeks, Duration.MoreThanTwoWeeks };
            var hotels = GetHotelList();
            return await GetPricesAsync(httpClient, durations, hotels, airports, persons);
        }

        private static async Task<List<TravelPrice>> GetPricesAsync(HttpClient httpClient, List<string> durations, List<Hotel> hotels, List<string> airports, int persons)
        {
            var travelPrices = new List<TravelPrice>();
            var paxAges = string.Join(",", Enumerable.Repeat("18", persons));
            var monthYearStrings = GetMonthYearStrings();

            var tasks = new List<Task<List<TravelPrice>>>();
            tasks.AddRange(durations.SelectMany(duration =>
                airports.SelectMany(airport =>
                    monthYearStrings.Select(monthYearString =>
                        GetPricesForHotelsAsync(httpClient, duration, hotels, airport, monthYearString, paxAges)
                    )
                )
            ).ToList());
            
            // Debug line
            // tasks.Add(GetPricesForHotelsAsync(httpClient, Duration.OneWeek, hotels, "CPH", "2025-01", paxAges));
                
            var results = await Task.WhenAll(tasks);

            foreach (var result in results)
            {
                travelPrices.AddRange(result);
            }

            return travelPrices;
        }

        private static async Task<List<TravelPrice>> GetPricesForHotelsAsync(HttpClient httpClient, string travelDuration, List<Hotel> hotels, string airport, string monthYearString, string paxAges)
        {
            var tasks = hotels.Select(hotel => GetPricesForHotelAndAirportAsync(httpClient, travelDuration, hotel, airport, monthYearString, paxAges));
            var results = await Task.WhenAll(tasks);
            return results.SelectMany(x => x).ToList();
        }

        private static async Task<List<TravelPrice>> GetPricesForHotelAndAirportAsync(HttpClient httpClient, string travelDuration, Hotel hotel, string airport, string monthYearString, string paxAges)
        {
            var url = $"{ApolloApiBasePath}/api/2.0/{SalesUnit}/SearchBox/DepartureDates?productTypeCodes=FlightAndHotel&accommodationCode={hotel.AccommodationCode}&departureDate={monthYearString}-01&departureAirportCode={airport}&duration={travelDuration}&catalogueItemId={hotel.HotelId}&departureDateRange=31&paxAges={paxAges}";
            var response = await httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode) return new List<TravelPrice>();

            var data = await response.Content.ReadFromJsonAsync<DepartureDatesResponse>();

            if (data?.DepartureDates == null || data.DepartureDates.Count == 0) return new List<TravelPrice>();

            var travels = data.DepartureDates
                .Where(d => !d.IsSoldOut && d.Price.HasValue)
                .Select(travelPriceDto => new TravelPrice(
                    travelPriceDto.Date,
                    travelPriceDto.Price ?? 0,
                    airport,
                    travelDuration,
                    hotel.DisplayName,
                    HotelLink(hotel.HotelUrl, travelPriceDto.Date, airport, travelDuration, hotel.HotelId, paxAges)))
                .ToList();

            if (travelDuration == Duration.MoreThanTwoWeeks)
            {
                travels = await Handle21And28DayTravelAsync(httpClient, travels, hotel, airport, paxAges);
            }

            return travels;
        }

        private static async Task<List<TravelPrice>> Handle21And28DayTravelAsync(HttpClient httpClient, List<TravelPrice> travels, Hotel hotel, string airport, string paxAges)
        {
            var updatedTravels = new List<TravelPrice>();

            foreach (var travel in travels)
            {
                var flightsUrl = $"{ApolloApiBasePath}/api/2.0/{SalesUnit}/Core/AccommodationSearch?accommodationCode={hotel.AccommodationCode}&departureAirportCode={airport}&departureDate={travel.Date:yyyy-MM-dd}&duration=21&paxAges={paxAges}";
                var flightsResponse = await httpClient.GetAsync(flightsUrl);

                if (!flightsResponse.IsSuccessStatusCode) continue;

                var accommodationSearch = await flightsResponse.Content.ReadFromJsonAsync<AccommodationSearchResponse>();
                if (accommodationSearch == null) continue;

                var alternativeDurationProductUrl = $"{ApolloApiBasePath}/api/2.0/{SalesUnit}/Core/AlternativeDurationProducts?productId={accommodationSearch.ProductId}&paxAges={paxAges}";
                var alternativeDurationProductResponse = await httpClient.GetAsync(alternativeDurationProductUrl);

                if (!alternativeDurationProductResponse.IsSuccessStatusCode) continue;

                var alternativeDurationProducts = await alternativeDurationProductResponse.Content.ReadFromJsonAsync<List<AlternativeDurationProductResponse>>();
                if (alternativeDurationProducts == null || alternativeDurationProducts.Count == 0) continue;

                foreach (var product in alternativeDurationProducts.Where(x => x.Duration >= 21))
                {
                    updatedTravels.Add(new TravelPrice(
                        travel.Date,
                        product.Price,
                        airport,
                        product.Duration.ToString(),
                        hotel.DisplayName,
                        HotelLink(hotel.HotelUrl, travel.Date, airport, product.Duration.ToString(), hotel.HotelId, paxAges)
                    ));
                }
            }

            return updatedTravels;
        }

        private static List<string> GetMonthYearStrings()
        {
            var monthYearStrings = new List<string>();
            var currentDate = DateTime.Now;

            for (int year = currentDate.Year; year <= currentDate.Year + 1; year++)
            {
                int startMonth = (year == currentDate.Year) ? currentDate.Month : 1;
                for (int month = startMonth; month <= 12; month++)
                {
                    monthYearStrings.Add($"{year}-{month:D2}");
                }
            }

            return monthYearStrings;
        }

        private static string HotelLink(string hotelUrl, DateTime date, string airport, string duration, string hotelId, string paxAges)
        {
            return $"https://www.apollorejser.dk/{hotelUrl}?departureDate={date:yyyy-MM-dd}&departureAirportCode={airport}&duration={duration}&catalogueItemId={hotelId}&departureDateRange=31&paxAges={paxAges}";
        }

        private static List<Hotel> GetHotelList()
        {
            return new List<Hotel>
            {
                new("Playitas Annexe (Fuerteventura - Spanien)", "530116", "ESPLYPLA01", "spanien/de-kanariske-oer/fuerteventura/playitas-resort/hoteller/playitas-annexe"),
                new("Playitas Resort (Fuerteventura - Spanien)", "160759", "ESPLYBAH01", "spanien/de-kanariske-oer/fuerteventura/playitas-resort/hoteller/playitas-resort")
            };
        }
    }


    public record Hotel(string DisplayName, string HotelId, string AccommodationCode, string HotelUrl);
    public record TravelPrice(DateTime Date, decimal Price, string Airport, string Duration, string Hotel, string Link);

    public record AlternativeDurationProductResponse(string DepartureDate, int Duration, decimal Price);
    public record AccommodationSearchResponse(string ProductId);
    public record DepartureDatesResponse(List<TravelPriceResponse> DepartureDates);
    public record TravelPriceResponse(DateTime Date, bool IsSoldOut, decimal? Price);


    public static class Duration
    {
        public const string OneWeek = "7";
        public const string TwoWeeks = "14";
        public const string MoreThanTwoWeeks = "21";
    }
}
