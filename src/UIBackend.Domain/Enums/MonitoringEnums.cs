namespace UIBackend.Domain.Enums;

public enum DeviceStatus
{
    Pending = 0,
    Starting = 1,
    Online = 2,
    Offline = 3,
    Error = 4,
    Stopped = 5
}

public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

public enum NotificationDispatchStatus
{
    NotRequired = 0,
    Pending = 1,
    Sent = 2,
    Failed = 3,
    Retrying = 4
}

public enum MonitoringJobState
{
    PendingStart = 0,
    Active = 1,
    Stopping = 2,
    Stopped = 3,
    Failed = 4
}
