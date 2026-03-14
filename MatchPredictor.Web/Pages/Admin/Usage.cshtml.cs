using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatchPredictor.Web.Pages.Admin;

public class UsageModel : PageModel
{
    private readonly IUserTrackingService _userTrackingService;

    public UsageModel(IUserTrackingService userTrackingService)
    {
        _userTrackingService = userTrackingService;
    }

    public UsageSnapshot UsageSnapshot { get; private set; } = new();

    public async Task OnGetAsync()
    {
        UsageSnapshot = await _userTrackingService.GetUsageSnapshotAsync();
    }
}
