using MatchPredictor.Domain.Interfaces;
using MatchPredictor.Domain.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatchPredictor.Web.Pages;

public class AiTipsterModel : PageModel
{
    private readonly IOperationsTipsterService _tipsterService;
    private readonly ILogger<AiTipsterModel> _logger;

    public AiTipsterModel(
        IOperationsTipsterService tipsterService,
        ILogger<AiTipsterModel> logger)
    {
        _tipsterService = tipsterService;
        _logger = logger;
    }

    public OperationsDossierDto? Dossier { get; private set; }

    public async Task OnGetAsync(CancellationToken ct = default)
    {
        try
        {
            Dossier = await _tipsterService.GetOperationsDossierAsync(null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load public AI Tipster dossier.");
            Dossier = null;
        }
    }
}
