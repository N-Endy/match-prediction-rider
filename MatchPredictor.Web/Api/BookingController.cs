using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace MatchPredictor.Web.Api;

[ApiController]
[Route("api/booking")]
public class BookingController : ControllerBase
{
    private readonly ISportyBetBookingService _bookingService;
    private readonly IUserTrackingService _userTrackingService;

    public BookingController(ISportyBetBookingService bookingService, IUserTrackingService userTrackingService)
    {
        _bookingService = bookingService;
        _userTrackingService = userTrackingService;
    }

    [HttpPost("book")]
    public async Task<IActionResult> Book([FromBody] BookingRequest request)
    {
        if (request.Selections.Count == 0)
        {
            return BadRequest(new BookingResult { Success = false, Message = "No games selected." });
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
