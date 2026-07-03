namespace MatchPredictor.Domain.Models;

public class Team
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? LeagueScope { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<TeamAlias> Aliases { get; set; } = new List<TeamAlias>();
}
