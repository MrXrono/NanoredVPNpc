using System.Text.Json;
using Serilog;
using SingBoxClient.Core.Constants;
using SingBoxClient.Core.Models;

namespace SingBoxClient.Core.Services;

/// <summary>
/// Manages user-defined and remote routing rules with persistence.
/// </summary>
public interface IRoutingService
{
    /// <summary>
    /// Return all rules sorted by <see cref="RoutingRule.Priority"/>.
    /// </summary>
    List<RoutingRule> GetRules();

    /// <summary>
    /// Add a new rule. Priority is assigned automatically.
    /// </summary>
    void AddRule(RoutingRule rule);

    /// <summary>
    /// Remove a rule by its <see cref="RoutingRule.Id"/>.
    /// </summary>
    void RemoveRule(string id);

    /// <summary>
    /// Replace an existing rule's properties (matched by Id).
    /// </summary>
    void UpdateRule(RoutingRule rule);

    /// <summary>
    /// Move a rule to a new position (0-based index) and reindex all priorities.
    /// </summary>
    void MoveRule(string id, int newIndex);

    /// <summary>
    /// Merge remote rules: remove old remote entries, insert new ones at the top,
    /// and push custom (non-remote) rules down.
    /// </summary>
    void MergeRemoteRules(List<RoutingRule> remote);

    /// <summary>
    /// Alias for <see cref="GetRules"/>. Used by Desktop ViewModels.
    /// </summary>
    List<RoutingRule> GetAllRules() => GetRules();

    /// <summary>
    /// Return only rules where <see cref="RoutingRule.IsEnabled"/> is true,
    /// sorted by priority. Used by Desktop ViewModels.
    /// </summary>
    List<RoutingRule> GetEnabledRules();

    /// <summary>
    /// Replace all current rules with the given list and persist to disk.
    /// Used by Desktop ViewModels.
    /// </summary>
    void SaveRules(List<RoutingRule> rules);

    /// <summary>
    /// Persist current rules to disk.
    /// </summary>
    void Save();

    /// <summary>
    /// Load rules from disk. Creates an empty list if the file does not exist.
    /// </summary>
    void Load();
}

/// <summary>
/// Default file-backed implementation that serializes rules to data/routing.json.
/// </summary>
public class RoutingService : IRoutingService
{
    private readonly ILogger _logger = Log.ForContext<RoutingService>();
    private readonly string _filePath;
    private List<RoutingRule> _rules = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RoutingService()
    {
        _filePath = Path.Combine(AppDefaults.DataDir, AppDefaults.RoutingFileName);
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    public List<RoutingRule> GetRules()
    {
        return _rules.OrderBy(r => r.Priority).ToList();
    }

    /// <inheritdoc />
    public List<RoutingRule> GetAllRules() => GetRules();

    /// <inheritdoc />
    public List<RoutingRule> GetEnabledRules()
    {
        return _rules.Where(r => r.IsEnabled).OrderBy(r => r.Priority).ToList();
    }

    /// <inheritdoc />
    public void SaveRules(List<RoutingRule> rules)
    {
        if (rules is null)
            throw new ArgumentNullException(nameof(rules));

        _rules = new List<RoutingRule>(rules);
        ReindexPriorities();
        Save();

        _logger.Debug("Rules replaced and saved ({Count} rules)", _rules.Count);
    }

    // ── Add ──────────────────────────────────────────────────────────────────

    public void AddRule(RoutingRule rule)
    {
        if (rule is null)
            throw new ArgumentNullException(nameof(rule));

        // Assign next available priority
        rule.Priority = _rules.Count > 0
            ? _rules.Max(r => r.Priority) + 1
            : 0;

        _rules.Add(rule);
        _logger.Debug("Rule added: {Type} {Value} → {Action} (priority {P})",
            rule.Type, rule.Value, rule.Action, rule.Priority);
    }

    // ── Remove ───────────────────────────────────────────────────────────────

    public void RemoveRule(string id)
    {
        var removed = _rules.RemoveAll(r => r.Id == id);
        if (removed > 0)
        {
            ReindexPriorities();
            _logger.Debug("Rule {Id} removed, priorities reindexed", id);
        }
        else
        {
            _logger.Warning("Rule {Id} not found for removal", id);
        }
    }

    // ── Update ───────────────────────────────────────────────────────────────

    public void UpdateRule(RoutingRule rule)
    {
        if (rule is null)
            throw new ArgumentNullException(nameof(rule));

        var index = _rules.FindIndex(r => r.Id == rule.Id);
        if (index < 0)
        {
            _logger.Warning("Rule {Id} not found for update", rule.Id);
            return;
        }

        _rules[index] = rule;
        _logger.Debug("Rule {Id} updated: {Type} {Value} → {Action}",
            rule.Id, rule.Type, rule.Value, rule.Action);
    }

    // ── Move ─────────────────────────────────────────────────────────────────

    public void MoveRule(string id, int newIndex)
    {
        var sorted = _rules.OrderBy(r => r.Priority).ToList();
        var rule = sorted.FirstOrDefault(r => r.Id == id);
        if (rule is null)
        {
            _logger.Warning("Rule {Id} not found for move", id);
            return;
        }

        sorted.Remove(rule);
        newIndex = Math.Clamp(newIndex, 0, sorted.Count);
        sorted.Insert(newIndex, rule);

        // Reassign priorities based on new positions
        for (int i = 0; i < sorted.Count; i++)
            sorted[i].Priority = i;

        _rules = sorted;
        _logger.Debug("Rule {Id} moved to index {Index}", id, newIndex);
    }

    // ── Merge remote ─────────────────────────────────────────────────────────

    public void MergeRemoteRules(List<RoutingRule> remote)
    {
        if (remote is null)
            throw new ArgumentNullException(nameof(remote));

        // Remove all existing remote rules
        _rules.RemoveAll(r => r.IsRemote);

        // Mark incoming rules as remote
        foreach (var rule in remote)
            rule.IsRemote = true;

        // Insert remote rules at the top (lowest priorities)
        var customRules = _rules.OrderBy(r => r.Priority).ToList();

        var merged = new List<RoutingRule>();

        // Remote rules get priorities 0..N-1
        for (int i = 0; i < remote.Count; i++)
        {
            remote[i].Priority = i;
            merged.Add(remote[i]);
        }

        // Custom rules get priorities N..N+M-1
        for (int i = 0; i < customRules.Count; i++)
        {
            customRules[i].Priority = remote.Count + i;
            merged.Add(customRules[i]);
        }

        _rules = merged;
        _logger.Information("Merged {Count} remote rules, {Custom} custom rules preserved",
            remote.Count, customRules.Count);
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(_rules, SerializerOptions);
            File.WriteAllText(_filePath, json);

            _logger.Debug("Routing rules saved to {Path} ({Count} rules)", _filePath, _rules.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save routing rules to {Path}", _filePath);
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _logger.Debug("Routing file not found at {Path}, starting with empty rules", _filePath);
                _rules = new List<RoutingRule>();
                return;
            }

            var json = File.ReadAllText(_filePath);
            _rules = JsonSerializer.Deserialize<List<RoutingRule>>(json, SerializerOptions)
                     ?? new List<RoutingRule>();

            _logger.Information("Loaded {Count} routing rules from {Path}", _rules.Count, _filePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load routing rules from {Path}", _filePath);
            _rules = new List<RoutingRule>();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void ReindexPriorities()
    {
        var sorted = _rules.OrderBy(r => r.Priority).ToList();
        for (int i = 0; i < sorted.Count; i++)
            sorted[i].Priority = i;

        _rules = sorted;
    }
}
