using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Curia.Models;
using Curia.Services;

namespace Curia.ViewModels;

public partial class PomodoroViewModel : ObservableObject, IDisposable
{
    private readonly PomodoroService _pomodoroService;
    private readonly ProjectDiscoveryService _discoveryService;
    private readonly ConfigService _configService;

    [ObservableProperty]
    private string timerDisplay = "25:00";

    [ObservableProperty]
    private string statusIcon = "▶";

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private bool isPaused;

    [ObservableProperty]
    private string todaySummary = "";

    [ObservableProperty]
    private ProjectInfo? selectedProject;

    [ObservableProperty]
    private int selectedDurationMinutes = 25;

    public ObservableCollection<ProjectInfo> Projects { get; } = [];

    public PomodoroService PomodoroService => _pomodoroService;

    // 外部からセッション完了ウィンドウを表示するためのコールバック
    public Action<PomodoroSession>? OnSessionCompleted { get; set; }

    public PomodoroViewModel(
        PomodoroService pomodoroService,
        ProjectDiscoveryService discoveryService,
        ConfigService configService)
    {
        _pomodoroService = pomodoroService;
        _discoveryService = discoveryService;
        _configService = configService;

        _pomodoroService.Tick += OnTick;
        _pomodoroService.SessionCompleted += OnSessionCompletedInternal;
    }

    public async Task InitAsync()
    {
        try
        {
            var all = await Task.Run(() => _discoveryService.GetProjectInfoList());
            var hidden = _configService.LoadHiddenProjects();
            Projects.Clear();
            foreach (var p in all.Where(p => !hidden.Contains(p.HiddenKey)))
                Projects.Add(p);

            if (SelectedProject == null && Projects.Count > 0)
                SelectedProject = Projects[0];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PomodoroViewModel] InitAsync failed: {ex.Message}");
        }
    }

    private void OnTick(TimeSpan remaining)
    {
        TimerDisplay = $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
    }

    private void OnSessionCompletedInternal(PomodoroSession session)
    {
        IsRunning = false;
        IsPaused = false;
        StatusIcon = "▶";
        ResetTimer(session.DurationMinutes);
        OnSessionCompleted?.Invoke(session);
        _ = RefreshTodaySummaryAsync();
    }

    [RelayCommand]
    public void Pause()
    {
        if (_pomodoroService.State == PomodoroState.Running)
        {
            _pomodoroService.Pause();
            IsPaused = true;
            StatusIcon = "⏸";
        }
        else if (_pomodoroService.State == PomodoroState.Paused)
        {
            _pomodoroService.Resume();
            IsPaused = false;
            StatusIcon = "▶";
        }
    }

    [RelayCommand]
    public void Interrupt()
    {
        _pomodoroService.Interrupt();
        IsRunning = false;
        IsPaused = false;
        StatusIcon = "▶";
        ResetTimer(SelectedDurationMinutes);
        _ = RefreshTodaySummaryAsync();
    }

    public void FinishEarly()
    {
        // SessionCompleted イベントが発火し OnSessionCompletedInternal が UI をリセットする
        _pomodoroService.FinishEarly();
    }

    public void StartSession(PomodoroSession session)
    {
        SelectedProject = Projects.FirstOrDefault(p => p.HiddenKey == session.ProjectKey)
                          ?? SelectedProject;
        SelectedDurationMinutes = session.DurationMinutes;

        _pomodoroService.Start(session);
        IsRunning = true;
        IsPaused = false;
        StatusIcon = "▶";
        TimerDisplay = $"{session.DurationMinutes:D2}:00";
    }

    public async Task StartBreakAsync(int minutes = 5)
    {
        var session = new PomodoroSession
        {
            DurationMinutes = minutes,
            IsBreak = true,
            ProjectKey = SelectedProject?.HiddenKey ?? "",
            ProjectName = SelectedProject?.Name ?? ""
        };
        _pomodoroService.Start(session);
        IsRunning = true;
        IsPaused = false;
        StatusIcon = "☕";
        TimerDisplay = $"{minutes:D2}:00";
        await Task.CompletedTask;
    }

    public async Task RefreshTodaySummaryAsync()
    {
        try
        {
            var summary = await _pomodoroService.GetDaySummaryAsync(DateTime.Today);
            TodaySummary = summary != null
                ? $"{summary.CompletedSessions} sessions / {summary.TotalFocusMinutes} min"
                : "";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PomodoroViewModel] RefreshTodaySummaryAsync failed: {ex.Message}");
        }
    }

    private void ResetTimer(int minutes)
    {
        TimerDisplay = $"{minutes:D2}:00";
    }

    public void Dispose()
    {
        _pomodoroService.Tick -= OnTick;
        _pomodoroService.SessionCompleted -= OnSessionCompletedInternal;
    }
}
