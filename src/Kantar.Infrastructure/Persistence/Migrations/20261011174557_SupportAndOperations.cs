using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kantar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupportAndOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ai");

            migrationBuilder.EnsureSchema(
                name: "sup");

            migrationBuilder.CreateSequence(
                name: "TicketNumbers",
                schema: "sup");

            migrationBuilder.CreateTable(
                name: "Answers",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    Provider = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: true),
                    AnswerText = table.Column<string>(type: "nvarchar(max)", maxLength: 6000, nullable: true),
                    LimitationsText = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CitationsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Answers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Answers_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "iam",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KbDocuments",
                schema: "sup",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Visibility = table.Column<byte>(type: "tinyint", nullable: false),
                    Module = table.Column<byte>(type: "tinyint", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ErrorCode = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    PublishedVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LatestVersionNo = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbDocuments", x => x.Id);
                    table.CheckConstraint("CK_KbDocuments_Module", "[Module] IN (1, 2, 3, 4, 5)");
                    table.CheckConstraint("CK_KbDocuments_Published", "[Status] <> 2 OR [PublishedVersionId] IS NOT NULL");
                    table.CheckConstraint("CK_KbDocuments_Status", "[Status] IN (1, 2, 3)");
                    table.CheckConstraint("CK_KbDocuments_Visibility", "[Visibility] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_KbDocuments_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "md",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "iam",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutboxConsumptions",
                schema: "ops",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Handler = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxConsumptions", x => new { x.MessageId, x.Handler });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    AvailableAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LeaseVersion = table.Column<long>(type: "bigint", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastErrorCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationRuns",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    BalancesChecked = table.Column<int>(type: "int", nullable: false),
                    FindingCount = table.Column<int>(type: "int", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tickets",
                schema: "sup",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RequesterUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssigneeUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Priority = table.Column<byte>(type: "tinyint", nullable: false),
                    Module = table.Column<byte>(type: "tinyint", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Screen = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: true),
                    ErrorCode = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: true, collation: "Latin1_General_100_BIN2"),
                    RelatedDocumentType = table.Column<byte>(type: "tinyint", nullable: true),
                    RelatedDocumentNumber = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true, collation: "Latin1_General_100_BIN2"),
                    DiagnosticJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.Id);
                    table.CheckConstraint("CK_Tickets_Module", "[Module] IN (1, 2, 3, 4, 5)");
                    table.CheckConstraint("CK_Tickets_Priority", "[Priority] IN (1, 2, 3, 4)");
                    table.CheckConstraint("CK_Tickets_Status", "[Status] IN (1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_Tickets_Users_AssigneeUserId",
                        column: x => x.AssigneeUserId,
                        principalSchema: "iam",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Users_RequesterUserId",
                        column: x => x.RequesterUserId,
                        principalSchema: "iam",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Usage",
                schema: "ai",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Provider = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: true),
                    Status = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    SourceCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Usage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KbDocumentVersions",
                schema: "sup",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNo = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    BodyMarkdown = table.Column<string>(type: "nvarchar(max)", maxLength: 256, nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbDocumentVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KbDocumentVersions_KbDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalSchema: "sup",
                        principalTable: "KbDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationFindings",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rule = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false),
                    Expected = table.Column<long>(type: "bigint", nullable: false),
                    Actual = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReconciliationFindings_ReconciliationRuns_RunId",
                        column: x => x.RunId,
                        principalSchema: "ops",
                        principalTable: "ReconciliationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketComments",
                schema: "sup",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    IsInternal = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    AiProvider = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    AiModel = table.Column<string>(type: "nvarchar(96)", maxLength: 96, nullable: true),
                    CitationsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketComments_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalSchema: "sup",
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketComments_Users_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalSchema: "iam",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KbChunks",
                schema: "sup",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SectionNo = table.Column<int>(type: "int", nullable: false),
                    Heading = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KbChunks_KbDocumentVersions_VersionId",
                        column: x => x.VersionId,
                        principalSchema: "sup",
                        principalTable: "KbDocumentVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KbChunks_KbDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalSchema: "sup",
                        principalTable: "KbDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Answers_ExpiresAt",
                schema: "ai",
                table: "Answers",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Answers_UserId",
                schema: "ai",
                table: "Answers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_KbChunks_DocumentId",
                schema: "sup",
                table: "KbChunks",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "UX_KbChunks_Section",
                schema: "sup",
                table: "KbChunks",
                columns: new[] { "VersionId", "SectionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KbDocuments_Status",
                schema: "sup",
                table: "KbDocuments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_KbDocuments_WarehouseId",
                schema: "sup",
                table: "KbDocuments",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "UX_KbDocuments_Slug",
                schema: "sup",
                table: "KbDocuments",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_KbDocumentVersions_No",
                schema: "sup",
                table: "KbDocumentVersions",
                columns: new[] { "DocumentId", "VersionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_CreatedAt",
                schema: "ops",
                table: "Notifications",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAt_AvailableAt_CreatedAt",
                schema: "ops",
                table: "OutboxMessages",
                columns: new[] { "ProcessedAt", "AvailableAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationFindings_RunId",
                schema: "ops",
                table: "ReconciliationFindings",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRuns_CompletedAt",
                schema: "ops",
                table: "ReconciliationRuns",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TicketComments_AuthorUserId",
                schema: "sup",
                table: "TicketComments",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketComments_TicketId_CreatedAt",
                schema: "sup",
                table: "TicketComments",
                columns: new[] { "TicketId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_AssigneeUserId",
                schema: "sup",
                table: "Tickets",
                column: "AssigneeUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CreatedAt_Id",
                schema: "sup",
                table: "Tickets",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_RequesterUserId",
                schema: "sup",
                table: "Tickets",
                column: "RequesterUserId");

            migrationBuilder.CreateIndex(
                name: "UX_Tickets_Number",
                schema: "sup",
                table: "Tickets",
                column: "Number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Answers",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "KbChunks",
                schema: "sup");

            migrationBuilder.DropTable(
                name: "Notifications",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "OutboxConsumptions",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "ReconciliationFindings",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "TicketComments",
                schema: "sup");

            migrationBuilder.DropTable(
                name: "Usage",
                schema: "ai");

            migrationBuilder.DropTable(
                name: "KbDocumentVersions",
                schema: "sup");

            migrationBuilder.DropTable(
                name: "ReconciliationRuns",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "Tickets",
                schema: "sup");

            migrationBuilder.DropTable(
                name: "KbDocuments",
                schema: "sup");

            migrationBuilder.DropSequence(
                name: "TicketNumbers",
                schema: "sup");
        }
    }
}
