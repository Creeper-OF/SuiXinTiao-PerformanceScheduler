namespace PerformanceScheduler.App.Services;

public enum NotificationIconType
{
    None = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

public interface IWindowsTrayNotificationService : IDisposable
{
    bool IsCreated { get; }
    void Initialize();
    void ShowNotification(string title, string message, NotificationIconType type = NotificationIconType.Info);
    void UpdateTooltip(string tooltipText);
}
