using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using MatchPredictor.Web.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatchPredictor.Web.Pages.Admin;

[PrivateSelectionsAuth]
public class OperationsModel : PageModel
{
    private readonly IOperationsTipsterService _tipsterService;
    private readonly ILogger<OperationsModel> _logger;

    public OperationsModel(
        IOperationsTipsterService tipsterService,
        ILogger<OperationsModel> logger)
    {
        _tipsterService = tipsterService;
        _logger = logger;
    }

    public OperationsDossierDto Dossier { get; private set; } = null!;

    [TempData]
    public string? StatusAlertMessage { get; set; }

    [TempData]
    public bool StatusAlertSuccess { get; set; }

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        Dossier = await _tipsterService.GetOperationsDossierAsync(null, ct);
    }

    public async Task<IActionResult> OnPostRefreshAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Admin requested on-demand re-analysis of AI Tipster Operations portfolio.");
            Dossier = await _tipsterService.RefreshOperationsDossierAsync(null, ct);
            StatusAlertMessage = "AI Tipster slate re-analyzed and booking codes refreshed successfully!";
            StatusAlertSuccess = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh AI Tipster Operations portfolio.");
            StatusAlertMessage = $"Refresh failed: {ex.Message}";
            StatusAlertSuccess = false;
        }

        return RedirectToPage("/Admin/Operations");
    }
}
