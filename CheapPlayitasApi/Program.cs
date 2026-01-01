using Microsoft.Extensions.Caching.Memory;
using System.Net;

namespace CheapPlayitasApi
{

    public class Program
    {
        // https://booking-guide-bff.prod.dertouristiknordic.com/swagger/index.html
        private const string ApolloApiBasePath = "https://booking-guide-bff.prod.dertouristiknordic.com";
        private const string SalesUnit = "apollorejserdk";

        public static async Task Main(string[] args)
        {
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
            builder.Services.AddHttpClient(Microsoft.Extensions.Options.Options.DefaultName, client =>
            {
                client.DefaultRequestHeaders.ConnectionClose = false;
            }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 30,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            });
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

                int persons = context.Request.Query.TryGetValue("persons", out var personsValue) && !string.IsNullOrEmpty(personsValue.ToString()) 
                    ? int.Parse(personsValue.ToString()) 
                    : 2;
                string cacheKey = $"PricesCache{persons}";

                var prices = await cache.GetOrCreateAsync(cacheKey, async entry =>
                {
                    entry.SetAbsoluteExpiration(TimeSpan.FromHours(20));
                    return await GetHotelsAndPricesAsync(httpClient, persons);
                }) ?? new List<TravelPrice>();

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
            // tasks.Add(GetPricesForHotelsAsync(httpClient, Duration.OneWeek, hotels, "CPH", "2025-11", paxAges));
            // tasks.Add(GetPricesForHotelsAsync(httpClient, Duration.TwoWeeks, hotels, "CPH", "2025-11", paxAges));
            // tasks.Add(GetPricesForHotelsAsync(httpClient, Duration.MoreThanTwoWeeks, hotels, "CPH", "2025-11", paxAges));
            
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
            var url = $"{ApolloApiBasePath}/api/3/{SalesUnit}/accommodation-uri/{hotel.AccommodationUri}/accommodation-code/{hotel.AccommodationCode}/departure-dates-for-accommodation?accommodationUri={hotel.AccommodationUri}&accommodationCode={hotel.AccommodationCode}&departureAirportCode={airport}&departureDate={monthYearString}-01&departureDateRange=31&duration={travelDuration}&productTypeCodes=FlightAndHotel&productTypeCodes=Cruise&paxAges={paxAges}&paxConfig=";
            var response = await httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode) return new List<TravelPrice>();

            var data = await response.Content.ReadFromJsonAsync<DepartureDatesResponse>();

            if (data?.DepartureDates == null || data.DepartureDates.Count == 0) return new List<TravelPrice>();

            var travels = new List<TravelPrice>();
            
            foreach (var departure in data.DepartureDates.Where(d => !d.IsSoldOut && d.Price.HasValue))
            {
                if (travelDuration == Duration.MoreThanTwoWeeks)
                {
                    var longDurationTravels = await Handle21And28DayTravelAsync(
                        httpClient, 
                        hotel, 
                        departure.Date,
                        departure.Price ?? 0,
                        airport, 
                        paxAges);

                    travels.AddRange(longDurationTravels);
                }
                else
                {
                    var productId = await GetProductIdAsync(httpClient, hotel, departure.Date, airport, travelDuration, paxAges);
                    if (productId != null)
                    {
                        travels.Add(new TravelPrice(
                            departure.Date,
                            departure.Price ?? 0,
                            airport,
                            travelDuration,
                            hotel.DisplayName,
                            HotelLink(hotel.HotelUrl, departure.Date, airport, travelDuration, hotel.HotelId, paxAges, productId)));
                    }
                }
            }

            return travels;
        }


        private static async Task<string?> GetProductIdAsync(
            HttpClient httpClient,
            Hotel hotel,
            DateTime departureDate,
            string airport,
            string duration,
            string paxAges)
        {
            var flightsUrl = $"{ApolloApiBasePath}/api/2.0/{SalesUnit}/Core/AccommodationSearch?accommodationCode={hotel.AccommodationCode}&departureAirportCode={airport}&departureDate={departureDate:yyyy-MM-dd}&duration={duration}&paxAges={paxAges}";
            var flightsResponse = await httpClient.GetAsync(flightsUrl);

            if (!flightsResponse.IsSuccessStatusCode) return null;

            var accommodationSearch = await flightsResponse.Content.ReadFromJsonAsync<AccommodationSearchResponse>();
            if (accommodationSearch == null || string.IsNullOrEmpty(accommodationSearch.ProductId)) return null;
            if (accommodationSearch.HotelStay?.Stay != int.Parse(duration)) return null;
            return accommodationSearch.ProductId;
        }

        private static async Task<List<TravelPrice>> Handle21And28DayTravelAsync(
            HttpClient httpClient,
            Hotel hotel,
            DateTime departureDate,
            decimal basePrice,
            string airport,
            string paxAges)
        {
            var travels = new List<TravelPrice>();
            var durations = new[] { "21", "28" };

            foreach (var duration in durations)
            {
                var productId = await GetProductIdAsync(httpClient, hotel, departureDate, airport, duration, paxAges);
                if (productId != null)
                {
                    travels.Add(new TravelPrice(
                        departureDate,
                        basePrice,
                        airport,
                        duration,
                        hotel.DisplayName,
                        HotelLink(hotel.HotelUrl, departureDate, airport, duration, hotel.HotelId, paxAges, productId)
                    ));
                }
            }

            return travels;
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

        private static string HotelLink(string hotelUrl, DateTime date, string airport, string duration, string hotelId, string paxAges, string productId)
        {
            // Return empty string if any required parameter is missing
            if (string.IsNullOrWhiteSpace(airport) || 
                string.IsNullOrWhiteSpace(duration) || 
                string.IsNullOrWhiteSpace(hotelId) || 
                string.IsNullOrWhiteSpace(paxAges) ||
                string.IsNullOrWhiteSpace(productId))
            {
                return string.Empty;
            }

            var accommodationUri = $"der:accommodation:dtno:{hotelId}";
            return "https://www.apollorejser.dk/booking-guide/core/select-unit-and-meal" +
                   $"?departureAirportCode={Uri.EscapeDataString(airport)}" +
                   $"&paxAges={Uri.EscapeDataString(paxAges)}" +
                   $"&searchProductCategoryCodes=FlightAndHotel" +
                   $"&searchProductCategoryCodes=Cruise" +
                   $"&departureDate={date:yyyy-MM-dd}" +
                   $"&duration={Uri.EscapeDataString(duration)}" +
                   $"&accommodationUri={Uri.EscapeDataString(accommodationUri)}" +
                   $"&searchType=Cached" +
                   $"&productId={Uri.EscapeDataString(productId)}";
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


    public record Hotel(string DisplayName, string HotelId, string AccommodationCode, string HotelUrl)
    {
        public string AccommodationUri => $"der:accommodation:dtno:{HotelId}";
    }
    public record TravelPrice(DateTime Date, decimal Price, string Airport, string Duration, string Hotel, string Link);

    public record AlternativeDurationProductResponse(string DepartureDate, int Duration, decimal Price);
    public record AccommodationSearchResponse(string ProductId, HotelStay HotelStay)
    {
        public AccommodationSearchResponse() : this(string.Empty, new HotelStay(0)) { }
    }

    public record HotelStay(int Stay)
    {
        public HotelStay() : this(0) { }
    }
    
    public record DepartureDatesResponse(List<TravelPriceResponse> DepartureDates)
    {
        public DepartureDatesResponse() : this(new List<TravelPriceResponse>()) { }
    }
    
    public record TravelPriceResponse(DateTime Date, bool IsSoldOut, decimal? Price)
    {
        public TravelPriceResponse() : this(DateTime.MinValue, false, null) { }
    };


    public static class Duration
    {
        public const string OneWeek = "7";
        public const string TwoWeeks = "14";
        public const string MoreThanTwoWeeks = "21";
    }
}
