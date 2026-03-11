using Polly;
using Polly.Extensions.Http;

namespace MatchPredictor.Web.Extensions;

public static class ServiceExtension
{
    // Add your extension methods here
    public static void AddHttpClientServices(this IServiceCollection services)
    {
        services.AddHttpClient("Groq", client => 
            {
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .AddPolicyHandler(HttpPolicyExtensions
                .HandleTransientHttpError()
                .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                .WaitAndRetryAsync(2, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))))
            .AddPolicyHandler(HttpPolicyExtensions
                .HandleTransientHttpError()
                .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30)));
        
        services.AddHttpClient("SportyBet", client => 
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddPolicyHandler(HttpPolicyExtensions
                .HandleTransientHttpError()
                .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))))
            .AddPolicyHandler(HttpPolicyExtensions
                .HandleTransientHttpError()
                .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30)));
    }

    public static void AddRedisMemoryCache(this IServiceCollection services, IConfiguration configuration)
    {
        var redisConn = configuration.GetConnectionString("RedisConnection");
        if (string.IsNullOrEmpty(redisConn) || redisConn == "localhost:6379")
        {
            // Use Server RAM as a fallback cache, cheaper and simpler for small user bases
            services.AddDistributedMemoryCache();
        }
        else
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConn;
                options.InstanceName = "MatchPredictor_";
            });
        }
    }
}