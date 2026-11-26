using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UIBackend.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceMonitoringAndAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonitoredDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FeedUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EncryptedStreamCredentials = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    MonitoringEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MlWorkerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ActiveJobId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessfulInferenceAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    ConfigurationVersion = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoredDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DetectionRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    JobId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DetectionType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    AnomalyScore = table.Column<double>(type: "double precision", nullable: true),
                    ModelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ModelVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    StructuredMetadataJson = table.Column<string>(type: "text", nullable: true),
                    SnapshotReference = table.Column<string>(type: "text", nullable: true),
                    ObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsTest = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DetectionRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DetectionRecords_MonitoredDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "MonitoredDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MonitoringJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalJobId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonitoringJobs_MonitoredDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "MonitoredDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlertRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DetectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    NotificationStatus = table.Column<int>(type: "integer", nullable: false),
                    NotificationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DeduplicationKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    LastOccurrenceAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsTest = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertRecords_DetectionRecords_DetectionId",
                        column: x => x.DetectionId,
                        principalTable: "DetectionRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlertRecords_MonitoredDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "MonitoredDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationOutboxItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AlertId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EventId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextRetryAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastResponseCode = table.Column<int>(type: "integer", nullable: true),
                    LastResponseBody = table.Column<string>(type: "text", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    SimulateTransientFailureOnce = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationOutboxItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationOutboxItems_AlertRecords_AlertId",
                        column: x => x.AlertId,
                        principalTable: "AlertRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertRecords_CreatedAtUtc",
                table: "AlertRecords",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AlertRecords_DeduplicationKey",
                table: "AlertRecords",
                column: "DeduplicationKey");

            migrationBuilder.CreateIndex(
                name: "IX_AlertRecords_DetectionId",
                table: "AlertRecords",
                column: "DetectionId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertRecords_DeviceId",
                table: "AlertRecords",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertRecords_IsRead",
                table: "AlertRecords",
                column: "IsRead");

            migrationBuilder.CreateIndex(
                name: "IX_AlertRecords_OwnerUserId",
                table: "AlertRecords",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertRecords_Severity",
                table: "AlertRecords",
                column: "Severity");

            migrationBuilder.CreateIndex(
                name: "IX_DetectionRecords_CreatedAtUtc",
                table: "DetectionRecords",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DetectionRecords_DeviceId",
                table: "DetectionRecords",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DetectionRecords_EventId",
                table: "DetectionRecords",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DetectionRecords_OwnerUserId",
                table: "DetectionRecords",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredDevices_CreatedAtUtc",
                table: "MonitoredDevices",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredDevices_OwnerUserId",
                table: "MonitoredDevices",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredDevices_Status",
                table: "MonitoredDevices",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringJobs_DeviceId",
                table: "MonitoringJobs",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringJobs_ExternalJobId",
                table: "MonitoringJobs",
                column: "ExternalJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringJobs_IdempotencyKey",
                table: "MonitoringJobs",
                column: "IdempotencyKey");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutboxItems_AlertId",
                table: "NotificationOutboxItems",
                column: "AlertId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutboxItems_EventId",
                table: "NotificationOutboxItems",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutboxItems_NextRetryAtUtc",
                table: "NotificationOutboxItems",
                column: "NextRetryAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutboxItems_Status",
                table: "NotificationOutboxItems",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonitoringJobs");

            migrationBuilder.DropTable(
                name: "NotificationOutboxItems");

            migrationBuilder.DropTable(
                name: "AlertRecords");

            migrationBuilder.DropTable(
                name: "DetectionRecords");

            migrationBuilder.DropTable(
                name: "MonitoredDevices");
        }
    }
}
