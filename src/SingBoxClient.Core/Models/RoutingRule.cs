using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SingBoxClient.Core.Models;

/// <summary>
/// Types of routing rules supported by sing-box.
/// </summary>
public enum RuleType
{
    Domain = 0,
    DomainSuffix = 1,
    IpCidr = 2,
    GeoIP = 3,
    GeoSite = 4
}

/// <summary>
/// Action to take when a routing rule matches.
/// </summary>
public enum RuleAction
{
    Proxy = 0,
    Direct = 1,
    Block = 2
}

/// <summary>
/// A single routing rule that determines how traffic is handled.
/// Implements INotifyPropertyChanged so DataGrid / bindings can detect changes.
/// </summary>
public class RoutingRule : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    // ── Backing fields ───────────────────────────────────────────────────

    private string _id = Guid.NewGuid().ToString();
    private RuleType _type;
    private string _value = string.Empty;
    private RuleAction _action = RuleAction.Proxy;
    private bool _isRemote;
    private bool _isEnabled = true;
    private int _priority = 100;

    // ── Properties ───────────────────────────────────────────────────────

    /// <summary>
    /// Unique rule identifier (GUID string).
    /// </summary>
    [JsonPropertyName("id")]
    public string Id
    {
        get => _id;
        set { if (_id != value) { _id = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Rule matching type.
    /// </summary>
    [JsonPropertyName("type")]
    public RuleType Type
    {
        get => _type;
        set { if (_type != value) { _type = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Value to match against (domain, IP CIDR, geo code, etc.).
    /// </summary>
    [JsonPropertyName("value")]
    public string Value
    {
        get => _value;
        set { if (_value != value) { _value = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Action to take when the rule matches.
    /// </summary>
    [JsonPropertyName("action")]
    public RuleAction Action
    {
        get => _action;
        set { if (_action != value) { _action = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Whether this rule was fetched from a remote configuration.
    /// </summary>
    [JsonPropertyName("is_remote")]
    public bool IsRemote
    {
        get => _isRemote;
        set { if (_isRemote != value) { _isRemote = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Whether this rule is currently active.
    /// </summary>
    [JsonPropertyName("is_enabled")]
    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Rule evaluation priority (lower = higher priority).
    /// </summary>
    [JsonPropertyName("priority")]
    public int Priority
    {
        get => _priority;
        set { if (_priority != value) { _priority = value; OnPropertyChanged(); } }
    }
}
