using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Api;

[ApiController]
[Route("api/booking")]
[EnableRateLimiting(RateLimitPolicies.Booking)]
public class BookingController : ControllerBase
{
    private const int DefaultMaxSelections = 50;
    private const int MaxFieldLength = 120;

    private readonly ISportyBetBookingService _bookingService;
    private readonly IUserTrackingService _userTrackingService;
    private readonly int _maxSelections;

    public BookingController(
        ISportyBetBookingService bookingService,
        IUserTrackingService userTrackingService,
        IConfiguration configuration)
    {
        _bookingService = bookingService;
        _userTrackingService = userTrackingService;
        _maxSelections = configuration.GetValue("SportyBet:MaxBookingSelections", DefaultMaxSelections);
        if (_maxSelections < 1)
        {
            _maxSelections = DefaultMaxSelections;
        }
    }

    [HttpPost("book")]
    public async Task<IActionResult> Book([FromBody] BookingRequest request)
    {
        // Same-origin guard: this endpoint drives an external booking flow and must
        // only be callable from our own pages, not cross-site.
        var origin = Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) &&
            (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
             !string.Equals(originUri.Host, Request.Host.Host, StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new BookingResult { Success = false, Message = "Cross-origin booking requests are not allowed." });
        }

        if (request.Selections.Count == 0)
        {
            return BadRequest(new BookingResult { Success = false, Message = "No games selected." });
        }

        if (request.Selections.Count > _maxSelections)
        {
            return BadRequest(new BookingResult { Success = false, Message = $"A maximum of {_maxSelections} selections is allowed." });
        }

        if (request.Selections.Any(selection =>
                string.IsNullOrWhiteSpace(selection.HomeTeam) ||
                string.IsNullOrWhiteSpace(selection.AwayTeam) ||
                selection.HomeTeam.Length > MaxFieldLength ||
                selection.AwayTeam.Length > MaxFieldLength ||
                selection.League.Length > MaxFieldLength ||
                selection.Market.Length > MaxFieldLength ||
                selection.Prediction.Length > MaxFieldLength ||
                (selection.ConfirmedSportyBetEventId?.Length ?? 0) > MaxFieldLength))
        {
            return BadRequest(new BookingResult { Success = false, Message = "One or more selections are invalid." });
        }

        await _userTrackingService.TrackEventAsync(
            HttpContext,
            "booking_attempt",
            "/betslip",
            new Dictionary<string, string?>
            {
                ["selectionCount"] = request.Selections.Count.ToString()
            });

        var result = await _bookingService.BookGamesAsync(request.Selections);

        await _userTrackingService.TrackEventAsync(
            HttpContext,
            result.Success ? "booking_success" : "booking_failure",
            "/betslip",
            new Dictionary<string, string?>
            {
                ["selectionCount"] = request.Selections.Count.ToString(),
                ["bookingCodePresent"] = (!string.IsNullOrWhiteSpace(result.BookingCode)).ToString()
            });

        return Ok(result);
    }
}
