using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UIBackend.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MediaType = table.Column<int>(type: "integer", nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    OriginalSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SignedSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StoragePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ThumbnailStoragePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CaptureRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CaptureDeviceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DeviceModel = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DeviceOs = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LocationLatLong = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    C2paManifestJson = table.Column<string>(type: "text", nullable: false),
                    C2paManifestSignature = table.Column<string>(type: "text", nullable: false),
                    C2paIssuer = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    C2paClaimAlgorithm = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    C2paStatus = table.Column<int>(type: "integer", nullable: false),
                    SynthIdStatus = table.Column<int>(type: "integer", nullable: false),
                    SynthIdConfidence = table.Column<double>(type: "double precision", nullable: true),
                    SynthIdRawResponse = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptureRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaptureRecords_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShareLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false),
                    AllowDownload = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AccessCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsRevoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShareLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShareLinks_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VerificationRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    VerifiedByUserId = table.Column<string>(type: "text", nullable: true),
                    MediaSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    C2paStatus = table.Column<int>(type: "integer", nullable: false),
                    HasValidHardBinding = table.Column<bool>(type: "boolean", nullable: false),
                    MatchesRegisteredCapture = table.Column<bool>(type: "boolean", nullable: false),
                    Issuer = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SignerCertificateThumbprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ClaimSummaryJson = table.Column<string>(type: "text", nullable: true),
                    SynthIdStatus = table.Column<int>(type: "integer", nullable: false),
                    SynthIdConfidence = table.Column<double>(type: "double precision", nullable: true),
                    StatusMessage = table.Column<string>(type: "text", nullable: false),
                    ClientIpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    VerifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VerificationRecords_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MediaAccessEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareLinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccessingUserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Referer = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsCrawlerOrBot = table.Column<bool>(type: "boolean", nullable: false),
                    OwnerNotified = table.Column<bool>(type: "boolean", nullable: false),
                    AccessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAccessEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaAccessEvents_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MediaAccessEvents_ShareLinks_ShareLinkId",
                        column: x => x.ShareLinkId,
                        principalTable: "ShareLinks",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaptureRecords_CapturedAtUtc",
                table: "CaptureRecords",
                column: "CapturedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CaptureRecords_MediaItemId",
                table: "CaptureRecords",
                column: "MediaItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaptureRecords_UserId",
                table: "CaptureRecords",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAccessEvents_AccessedAtUtc",
                table: "MediaAccessEvents",
                column: "AccessedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAccessEvents_MediaItemId",
                table: "MediaAccessEvents",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAccessEvents_ShareLinkId",
                table: "MediaAccessEvents",
                column: "ShareLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_CreatedAtUtc",
                table: "MediaItems",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_OriginalSha256",
                table: "MediaItems",
                column: "OriginalSha256");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_SignedSha256",
                table: "MediaItems",
                column: "SignedSha256");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_UserId",
                table: "MediaItems",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinks_CreatedByUserId",
                table: "ShareLinks",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinks_MediaItemId",
                table: "ShareLinks",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinks_Token",
                table: "ShareLinks",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VerificationRecords_MediaItemId",
                table: "VerificationRecords",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationRecords_MediaSha256",
                table: "VerificationRecords",
                column: "MediaSha256");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationRecords_VerifiedAtUtc",
                table: "VerificationRecords",
                column: "VerifiedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationRecords_VerifiedByUserId",
                table: "VerificationRecords",
                column: "VerifiedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaptureRecords");

            migrationBuilder.DropTable(
                name: "MediaAccessEvents");

            migrationBuilder.DropTable(
                name: "VerificationRecords");

            migrationBuilder.DropTable(
                name: "ShareLinks");

            migrationBuilder.DropTable(
                name: "MediaItems");
        }
    }
}
