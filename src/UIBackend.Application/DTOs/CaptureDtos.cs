using UIBackend.Domain.Enums;

namespace UIBackend.Application.DTOs;

public record RegisterCaptureRequest(
    Guid? MediaItemId,
    SourceType SourceType,
    string? CaptureDeviceId,
    string? DeviceModel,
    string? DeviceOs,
    string? LocationLatLong,
    DateTime? CapturedAtUtc,
    string? OriginalSha256
);

public record CaptureResponse(
    Guid Id,
    Guid MediaItemId,
    string UserId,
    SourceType SourceType,
    MediaType MediaType,
    DateTime CapturedAtUtc,
    C2paStatus C2paStatus,
    SynthIdStatus SynthIdStatus,
    double? SynthIdConfidence,
    string C2paIssuer,
    string OriginalSha256,
    string? SignedSha256,
    long FileSizeBytes,
    string FileName,
    string MimeType,
    string? DeviceModel,
    string? DeviceOs,
    string? LocationLatLong,
    int ShareLinksCount,
    int AccessEventsCount,
    DateTime CreatedAtUtc
);

public record CaptureListResponse(
    IReadOnlyList<CaptureResponse> Items,
    int TotalCount,
    int Page,
    int PageSize
);
