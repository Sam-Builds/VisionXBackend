using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UIBackend.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EvidenceVerificationUpgrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OwnerNotificationEnabled",
                table: "ShareLinks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "RevokedAtUtc",
                table: "ShareLinks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShareTokenHash",
                table: "ShareLinks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AppVersion",
                table: "MediaItems",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceKeyId",
                table: "MediaItems",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalArtifactPath",
                table: "MediaItems",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PerceptualHash",
                table: "MediaItems",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlatformAttestationStatus",
                table: "MediaItems",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VerificationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    MediaItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    InputFileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InputPerceptualHash = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    InputFormat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InputSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    OverallAssessment = table.Column<int>(type: "integer", nullable: false),
                    Explanation = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VerificationRuns_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AiDetectionChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VerificationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProviderVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DetectionStatus = table.Column<int>(type: "integer", nullable: false),
                    ResultLabel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    ModelDetected = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EvidenceJson = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    Limitations = table.Column<string>(type: "text", nullable: true),
                    CheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiDetectionChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiDetectionChecks_VerificationRuns_VerificationRunId",
                        column: x => x.VerificationRunId,
                        principalTable: "VerificationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ForensicChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VerificationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Result = table.Column<int>(type: "integer", nullable: false),
                    FindingsJson = table.Column<string>(type: "text", nullable: true),
                    RegionDataJson = table.Column<string>(type: "text", nullable: true),
                    ExifAnomaliesJson = table.Column<string>(type: "text", nullable: true),
                    Limitations = table.Column<string>(type: "text", nullable: true),
                    CheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForensicChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ForensicChecks_VerificationRuns_VerificationRunId",
                        column: x => x.VerificationRunId,
                        principalTable: "VerificationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaMatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VerificationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    MatchedMediaItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    MatchMethod = table.Column<int>(type: "integer", nullable: false),
                    Similarity = table.Column<double>(type: "double precision", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    ExactByteMatch = table.Column<bool>(type: "boolean", nullable: false),
                    HasValidEmbeddedCredentials = table.Column<bool>(type: "boolean", nullable: false),
                    DiffersFromOriginal = table.Column<bool>(type: "boolean", nullable: false),
                    IsAmbiguous = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaMatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaMatches_MediaItems_MatchedMediaItemId",
                        column: x => x.MatchedMediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MediaMatches_VerificationRuns_VerificationRunId",
                        column: x => x.VerificationRunId,
                        principalTable: "VerificationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProvenanceChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VerificationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ManifestPresent = table.Column<bool>(type: "boolean", nullable: false),
                    StructureValid = table.Column<bool>(type: "boolean", nullable: false),
                    SignatureValid = table.Column<bool>(type: "boolean", nullable: false),
                    SignerTrusted = table.Column<bool>(type: "boolean", nullable: false),
                    SignerIdentity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    HardBindingValid = table.Column<bool>(type: "boolean", nullable: false),
                    SoftBindingMatch = table.Column<bool>(type: "boolean", nullable: false),
                    IsDerivative = table.Column<bool>(type: "boolean", nullable: false),
                    RecordsAiActions = table.Column<bool>(type: "boolean", nullable: false),
                    RecordedActionsJson = table.Column<string>(type: "text", nullable: true),
                    IngredientRelationshipsJson = table.Column<string>(type: "text", nullable: true),
                    ProvenanceDetailsJson = table.Column<string>(type: "text", nullable: true),
                    Limitations = table.Column<string>(type: "text", nullable: true),
                    CheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvenanceChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProvenanceChecks_VerificationRuns_VerificationRunId",
                        column: x => x.VerificationRunId,
                        principalTable: "VerificationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinks_ShareTokenHash",
                table: "ShareLinks",
                column: "ShareTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_PerceptualHash",
                table: "MediaItems",
                column: "PerceptualHash");

            migrationBuilder.CreateIndex(
                name: "IX_AiDetectionChecks_VerificationRunId",
                table: "AiDetectionChecks",
                column: "VerificationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ForensicChecks_VerificationRunId",
                table: "ForensicChecks",
                column: "VerificationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaMatches_MatchedMediaItemId",
                table: "MediaMatches",
                column: "MatchedMediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaMatches_VerificationRunId",
                table: "MediaMatches",
                column: "VerificationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ProvenanceChecks_VerificationRunId",
                table: "ProvenanceChecks",
                column: "VerificationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationRuns_InputHash",
                table: "VerificationRuns",
                column: "InputHash");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationRuns_MediaItemId",
                table: "VerificationRuns",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationRuns_StartedAtUtc",
                table: "VerificationRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationRuns_UserId",
                table: "VerificationRuns",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiDetectionChecks");

            migrationBuilder.DropTable(
                name: "ForensicChecks");

            migrationBuilder.DropTable(
                name: "MediaMatches");

            migrationBuilder.DropTable(
                name: "ProvenanceChecks");

            migrationBuilder.DropTable(
                name: "VerificationRuns");

            migrationBuilder.DropIndex(
                name: "IX_ShareLinks_ShareTokenHash",
                table: "ShareLinks");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_PerceptualHash",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "OwnerNotificationEnabled",
                table: "ShareLinks");

            migrationBuilder.DropColumn(
                name: "RevokedAtUtc",
                table: "ShareLinks");

            migrationBuilder.DropColumn(
                name: "ShareTokenHash",
                table: "ShareLinks");

            migrationBuilder.DropColumn(
                name: "AppVersion",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "DeviceKeyId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "OriginalArtifactPath",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "PerceptualHash",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "PlatformAttestationStatus",
                table: "MediaItems");
        }
    }
}
