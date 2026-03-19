using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatchPredictor.Web.Pages.Admin;

public class UsageModel : PageModel
{
    private readonly IUserTrackingService _userTrackingService;
    public bool TrackingEnabled { get; private set; }

    public UsageModel(IUserTrackingService userTrackingService)
    {
        _userTrackingService = userTrackingService;
    }

    public UsageSnapshot UsageSnapshot { get; private set; } = new();

    public async Task OnGetAsync()
    {
        TrackingEnabled = _userTrackingService.IsEnabled;
        UsageSnapshot = await _userTrackingService.GetUsageSnapshotAsync();
    }
}
