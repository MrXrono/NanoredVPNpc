using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Text.RegularExpressions;
using ReactiveUI;
using Serilog;
using SingBoxClient.Core.Constants;
using SingBoxClient.Core.Models;
using SingBoxClient.Core.Services;

namespace SingBoxClient.Desktop.ViewModels;

/// <summary>
/// ViewModel for the routing rules management page — add, remove, toggle, reorder, and sync rules.
/// </summary>
public class RoutingViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<RoutingViewModel>();

    private readonly IRoutingService _routingService;
    private readonly IRemoteConfigService _remoteConfigService;
    private readonly ISettingsService _settingsService;
    private readonly ISingBoxProcessManager _processManager;

    // Regex for detecting IP CIDR notation (e.g. 192.168.0.0/24, 2001:db8::/32)
    private static readonly Regex IpCidrPattern = new(
        @"^[\d.:a-fA-F]+/\d{1,3}$", RegexOptions.Compiled);

    // ── Properties ────────────────────────────────────────────────────────

    private ObservableCollection<RoutingRule> _rules = new();
    public ObservableCollection<RoutingRule> Rules
    {
        get => _rules;
        set => this.RaiseAndSetIfChanged(ref _rules, value);
    }

    private bool _isRemoteConfigEnabled;
    public bool IsRemoteConfigEnabled
    {
        get => _isRemoteConfigEnabled;
        set => this.RaiseAndSetIfChanged(ref _isRemoteConfigEnabled, value);
    }

    private string _newRuleValue = string.Empty;
    public string NewRuleValue
    {
        get => _newRuleValue;
        set => this.RaiseAndSetIfChanged(ref _newRuleValue, value);
    }

    private RuleAction _newRuleAction = RuleAction.Proxy;
    public RuleAction NewRuleAction
    {
        get => _newRuleAction;
        set => this.RaiseAndSetIfChanged(ref _newRuleAction, value);
    }

    /// <summary>
    /// List of rule action names for the Action ComboBox in DataGrid.
    /// </summary>
    public ObservableCollection<string> RuleActions { get; } = new(Enum.GetNames<RuleAction>());

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> AddRuleCommand { get; }
    public ReactiveCommand<string, Unit> RemoveRuleCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> SyncRemoteConfigCommand { get; }
    public ReactiveCommand<string, Unit> ToggleRuleCommand { get; }
    public ReactiveCommand<string, Unit> MoveRuleUpCommand { get; }
    public ReactiveCommand<string, Unit> MoveRuleDownCommand { get; }

    /// <summary>
    /// Alias for SyncRemoteConfigCommand — used by the View's Sync button binding.
    /// </summary>
    public ReactiveCommand<Unit, Unit> SyncCommand => SyncRemoteConfigCommand;

    /// <summary>
    /// Deletes a routing rule by accepting the full RoutingRule object and extracting its Id.
    /// </summary>
    public ReactiveCommand<RoutingRule, Unit> DeleteRuleCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────

    public RoutingViewModel(
        IRoutingService routingService,
        IRemoteConfigService remoteConfigService,
        ISettingsService settingsService,
        ISingBoxProcessManager processManager)
    {
        _routingService = routingService ?? throw new ArgumentNullException(nameof(routingService));
        _remoteConfigService = remoteConfigService ?? throw new ArgumentNullException(nameof(remoteConfigService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));

        AddRuleCommand = ReactiveCommand.Create(AddRule);
        RemoveRuleCommand = ReactiveCommand.Create<string>(RemoveRule);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        CancelCommand = ReactiveCommand.Create(LoadRules);
        SyncRemoteConfigCommand = ReactiveCommand.CreateFromTask(SyncRemoteConfigAsync);
        ToggleRuleCommand = ReactiveCommand.Create<string>(ToggleRule);
        MoveRuleUpCommand = ReactiveCommand.Create<string>(MoveRuleUp);
        MoveRuleDownCommand = ReactiveCommand.Create<string>(MoveRuleDown);
        DeleteRuleCommand = ReactiveCommand.Create<RoutingRule>(rule => RemoveRule(rule.Id));

        IsRemoteConfigEnabled = _settingsService.Current.RemoteConfigEnabled;

        LoadRules();
    }

    // ── Load ─────────────────────────────────────────────────────────────

    private void LoadRules()
    {
        try
        {
            var rules = _routingService.GetAllRules();
            Rules = new ObservableCollection<RoutingRule>(rules.OrderBy(r => r.Priority));
            Logger.Debug("Loaded {Count} routing rules", Rules.Count);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load routing rules");
        }
    }

    // ── Add Rule ─────────────────────────────────────────────────────────

    private void AddRule()
    {
        try
        {
            var value = NewRuleValue?.Trim();
            if (string.IsNullOrEmpty(value))
                return;

            var ruleType = DetectRuleType(value);

            var rule = new RoutingRule
            {
                Type = ruleType,
                Value = value,
                Action = NewRuleAction,
                IsEnabled = true,
                IsRemote = false,
                Priority = Rules.Count > 0 ? Rules.Max(r => r.Priority) + 1 : 1
            };

            Rules.Add(rule);
            NewRuleValue = string.Empty;

            Logger.Information("Added routing rule: {Type} {Value} -> {Action}", ruleType, value, NewRuleAction);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to add routing rule");
        }
    }

    // ── Remove Rule ──────────────────────────────────────────────────────

    private void RemoveRule(string id)
    {
        try
        {
            var rule = Rules.FirstOrDefault(r => r.Id == id);
            if (rule is not null)
            {
                Rules.Remove(rule);
                Logger.Information("Removed routing rule: {Id} ({Value})", id, rule.Value);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to remove routing rule {Id}", id);
        }
    }

    // ── Toggle Rule ──────────────────────────────────────────────────────

    private void ToggleRule(string id)
    {
        try
        {
            var rule = Rules.FirstOrDefault(r => r.Id == id);
            if (rule is not null)
            {
                rule.IsEnabled = !rule.IsEnabled;

                // Force collection refresh for UI update
                var index = Rules.IndexOf(rule);
                Rules[index] = rule;

                _ = SaveAsync();

                Logger.Debug("Toggled rule {Id}: enabled={Enabled}", id, rule.IsEnabled);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to toggle routing rule {Id}", id);
        }
    }

    // ── Move Up / Down ───────────────────────────────────────────────────

    private void MoveRuleUp(string id)
    {
        try
        {
            var index = -1;
            for (int i = 0; i < Rules.Count; i++)
            {
                if (Rules[i].Id == id)
                {
                    index = i;
                    break;
                }
            }

            if (index > 0)
            {
                Rules.Move(index, index - 1);
                RecomputePriorities();
                Logger.Debug("Moved rule {Id} up to index {Index}", id, index - 1);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to move rule up: {Id}", id);
        }
    }

    private void MoveRuleDown(string id)
    {
        try
        {
            var index = -1;
            for (int i = 0; i < Rules.Count; i++)
            {
                if (Rules[i].Id == id)
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0 && index < Rules.Count - 1)
            {
                Rules.Move(index, index + 1);
                RecomputePriorities();
                Logger.Debug("Moved rule {Id} down to index {Index}", id, index + 1);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to move rule down: {Id}", id);
        }
    }

    // ── Save ─────────────────────────────────────────────────────────────

    private async Task SaveAsync()
    {
        try
        {
            RecomputePriorities();
            _routingService.SaveRules(Rules.ToList());

            Logger.Information("Saved {Count} routing rules", Rules.Count);

            // Restart sing-box if currently connected
            if (_processManager.IsRunning)
            {
                var configPath = System.IO.Path.Combine(AppDefaults.ConfigDir, AppDefaults.ConfigFileName);
                await _processManager.RestartAsync(configPath);
                Logger.Information("sing-box restarted after routing rules change");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to save routing rules");
        }
    }

    // ── Sync Remote Config ───────────────────────────────────────────────

    private async Task SyncRemoteConfigAsync()
    {
        try
        {
            Logger.Information("Syncing remote routing configuration...");

            var remoteRules = await _remoteConfigService.FetchRoutingRulesAsync();
            if (remoteRules is null || remoteRules.Count == 0)
            {
                Logger.Information("No remote routing rules received");
                return;
            }

            // Remove existing remote rules, keep local ones
            var localRules = Rules.Where(r => !r.IsRemote).ToList();

            // Mark all incoming rules as remote
            foreach (var rule in remoteRules)
            {
                rule.IsRemote = true;
            }

            // Merge: local rules first, then remote
            var merged = localRules.Concat(remoteRules).ToList();
            Rules = new ObservableCollection<RoutingRule>(merged);

            RecomputePriorities();
            _routingService.SaveRules(Rules.ToList());

            Logger.Information("Synced {Remote} remote rules, total {Total} rules",
                remoteRules.Count, Rules.Count);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to sync remote routing configuration");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Auto-detect the rule type from the value string.
    /// *.domain → DomainSuffix, IP/mask → IpCidr, plain domain → Domain.
    /// </summary>
    private static RuleType DetectRuleType(string value)
    {
        if (value.StartsWith("*.") || value.StartsWith("."))
            return RuleType.DomainSuffix;

        if (IpCidrPattern.IsMatch(value))
            return RuleType.IpCidr;

        if (value.StartsWith("geoip:"))
            return RuleType.GeoIP;

        if (value.StartsWith("geosite:"))
            return RuleType.GeoSite;

        return RuleType.Domain;
    }

    private void RecomputePriorities()
    {
        for (int i = 0; i < Rules.Count; i++)
        {
            Rules[i].Priority = i + 1;
        }
    }
}
