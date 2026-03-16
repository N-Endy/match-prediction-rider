using System.Reflection;
using MatchPredictor.Web.Api;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace MatchPredictor.Tests.Integration;

public class ApiRateLimitPolicyTests
{
    [Fact]
    public void AiChatController_HasAiChatRateLimitPolicy()
    {
        AssertControllerPolicy<AiChatController>(RateLimitPolicies.AiChat);
    }

    [Fact]
    public void BookingController_HasBookingRateLimitPolicy()
    {
        AssertControllerPolicy<BookingController>(RateLimitPolicies.Booking);
    }

    [Fact]
    public void TrackingController_HasTrackingRateLimitPolicy()
    {
        AssertControllerPolicy<TrackingController>(RateLimitPolicies.Tracking);
    }

    private static void AssertControllerPolicy<TController>(string expectedPolicy)
    {
        var attribute = typeof(TController)
            .GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(expectedPolicy, attribute!.PolicyName);
    }
}
