using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ShopFlow.Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ref");

            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.EnsureSchema(
                name: "messaging");

            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "Carriers",
                schema: "ref",
                columns: table => new
                {
                    Code = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Carriers", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Categories",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ParentId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Categories_Categories_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "catalog",
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Countries",
                schema: "ref",
                columns: table => new
                {
                    Code = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Countries", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Currencies",
                schema: "ref",
                columns: table => new
                {
                    Code = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MinorUnits = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Currencies", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                schema: "messaging",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Consumer = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.Consumer, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "messaging",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageType = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    ContractVersion = table.Column<int>(type: "int", nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    CausationId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    Source = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SessionId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    TraceParent = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    TraceState = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: true),
                    Destination = table.Column<string>(type: "varchar(260)", unicode: false, maxLength: 260, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClaimedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClaimedBy = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "Reservations",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseCode = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockReceipts",
                schema: "inventory",
                columns: table => new
                {
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseCode = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Sku = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockReceipts", x => x.ReceiptId);
                });

            migrationBuilder.CreateTable(
                name: "TaxRates",
                schema: "ref",
                columns: table => new
                {
                    CountryCode = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: false),
                    Category = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRates", x => new { x.CountryCode, x.Category });
                });

            migrationBuilder.CreateTable(
                name: "Warehouses",
                schema: "inventory",
                columns: table => new
                {
                    Code = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Warehouses", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                schema: "catalog",
                columns: table => new
                {
                    Sku = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    PriceAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PriceCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Sku);
                    table.ForeignKey(
                        name: "FK_Products_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "catalog",
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReservationLines",
                schema: "inventory",
                columns: table => new
                {
                    Sku = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservationLines", x => new { x.ReservationId, x.Sku });
                    table.ForeignKey(
                        name: "FK_ReservationLines_Reservations_ReservationId",
                        column: x => x.ReservationId,
                        principalSchema: "inventory",
                        principalTable: "Reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StockItems",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WarehouseCode = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Sku = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    OnHand = table.Column<int>(type: "int", nullable: false),
                    Reserved = table.Column<int>(type: "int", nullable: false),
                    ReorderThreshold = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockItems", x => x.Id);
                    table.CheckConstraint("CK_StockItems_OnHand", "[OnHand] >= 0");
                    table.CheckConstraint("CK_StockItems_Reserved", "[Reserved] >= 0 AND [Reserved] <= [OnHand]");
                    table.ForeignKey(
                        name: "FK_StockItems_Products_Sku",
                        column: x => x.Sku,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Sku",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockItems_Warehouses_WarehouseCode",
                        column: x => x.WarehouseCode,
                        principalSchema: "inventory",
                        principalTable: "Warehouses",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "ref",
                table: "Carriers",
                columns: new[] { "Code", "Name" },
                values: new object[,]
                {
                    { "DHL", "DHL Parcel" },
                    { "DPD", "DPD Polska" },
                    { "INPOST", "InPost" }
                });

            migrationBuilder.InsertData(
                schema: "catalog",
                table: "Categories",
                columns: new[] { "Id", "Name", "ParentId" },
                values: new object[,]
                {
                    { 1, "Dom", null },
                    { 3, "Elektronika", null }
                });

            migrationBuilder.InsertData(
                schema: "ref",
                table: "Countries",
                columns: new[] { "Code", "Name" },
                values: new object[,]
                {
                    { "CZ", "Czechia" },
                    { "DE", "Germany" },
                    { "LT", "Lithuania" },
                    { "PL", "Poland" },
                    { "SK", "Slovakia" }
                });

            migrationBuilder.InsertData(
                schema: "ref",
                table: "Currencies",
                columns: new[] { "Code", "MinorUnits", "Name" },
                values: new object[,]
                {
                    { "CZK", 2, "Czech koruna" },
                    { "EUR", 2, "Euro" },
                    { "PLN", 2, "Polish zloty" }
                });

            migrationBuilder.InsertData(
                schema: "ref",
                table: "TaxRates",
                columns: new[] { "Category", "CountryCode", "Rate" },
                values: new object[,]
                {
                    { "reduced", "DE", 0.07m },
                    { "standard", "DE", 0.19m },
                    { "reduced", "PL", 0.08m },
                    { "standard", "PL", 0.23m }
                });

            migrationBuilder.InsertData(
                schema: "inventory",
                table: "Warehouses",
                columns: new[] { "Code", "IsActive", "Name", "Priority" },
                values: new object[,]
                {
                    { "KRK-1", true, "Krakow Balice", 2 },
                    { "WAW-1", true, "Warsaw Okecie", 1 }
                });

            migrationBuilder.InsertData(
                schema: "catalog",
                table: "Categories",
                columns: new[] { "Id", "Name", "ParentId" },
                values: new object[] { 2, "Kuchnia", 1 });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_ParentId",
                schema: "catalog",
                table: "Categories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_ProcessedAt",
                schema: "messaging",
                table: "InboxMessages",
                column: "ProcessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_MessageId",
                schema: "messaging",
                table: "OutboxMessages",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Pending",
                schema: "messaging",
                table: "OutboxMessages",
                column: "Sequence",
                filter: "[PublishedAt] IS NULL")
                .Annotation("SqlServer:Include", new[] { "ClaimedUntil", "Attempts" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_PublishedAt",
                schema: "messaging",
                table: "OutboxMessages",
                column: "PublishedAt",
                filter: "[PublishedAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Products_CategoryId_IsActive",
                schema: "catalog",
                table: "Products",
                columns: new[] { "CategoryId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_ActiveExpiry",
                schema: "inventory",
                table: "Reservations",
                column: "ExpiresAt",
                filter: "[Status] = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_OrderId",
                schema: "inventory",
                table: "Reservations",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockItems_Sku",
                schema: "inventory",
                table: "StockItems",
                column: "Sku")
                .Annotation("SqlServer:Include", new[] { "OnHand", "Reserved" });

            migrationBuilder.CreateIndex(
                name: "IX_StockItems_WarehouseCode_Sku",
                schema: "inventory",
                table: "StockItems",
                columns: new[] { "WarehouseCode", "Sku" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Carriers",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "Countries",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "Currencies",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "InboxMessages",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "ReservationLines",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockItems",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockReceipts",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "TaxRates",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "Reservations",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Products",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Warehouses",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Categories",
                schema: "catalog");
        }
    }
}
