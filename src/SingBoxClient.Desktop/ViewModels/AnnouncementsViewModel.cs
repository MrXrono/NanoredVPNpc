using System;
using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using Serilog;
using SingBoxClient.Core.Models;
using SingBoxClient.Core.Services;

namespace SingBoxClient.Desktop.ViewModels;

/// <summary>
/// ViewModel for the announcements panel — displays server-side notifications.
/// </summary>
public class AnnouncementsViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<AnnouncementsViewModel>();

    private readonly IAnnouncementService _announcementService;

    // ── Properties ────────────────────────────────────────────────────────

    private ObservableCollection<Announcement> _announcements = new();
    public ObservableCollection<Announcement> Announcements
    {
        get => _announcements;
        set => this.RaiseAndSetIfChanged(ref _announcements, value);
    }

    // ── Close Action ────────────────────────────────────────────────────

    /// <summary>
    /// Action invoked to close the host window. Set by the code-behind.
    /// </summary>
    public Action? CloseAction { get; set; }

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> MarkAllReadCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────

    public AnnouncementsViewModel(IAnnouncementService announcementService)
    {
        _announcementService = announcementService ?? throw new ArgumentNullException(nameof(announcementService));

        MarkAllReadCommand = ReactiveCommand.Create(MarkAllRead);
        CloseCommand = ReactiveCommand.Create(() => { CloseAction?.Invoke(); });

        LoadAnnouncements();
    }

    // ── Private ──────────────────────────────────────────────────────────

    private void LoadAnnouncements()
    {
        try
        {
            var items = _announcementService.GetAll();
            Announcements = new ObservableCollection<Announcement>(items);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load announcements");
        }
    }

    private void MarkAllRead()
    {
        try
        {
            foreach (var item in Announcements)
            {
                item.IsRead = true;
            }

            _announcementService.MarkAllRead();
            this.RaisePropertyChanged(nameof(Announcements));

            Logger.Information("All announcements marked as read");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to mark announcements as read");
        }
    }
}
