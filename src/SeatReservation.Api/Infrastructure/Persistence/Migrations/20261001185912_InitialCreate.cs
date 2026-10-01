using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeatReservation.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    price_paise = table.Column<long>(type: "bigint", nullable: false),
                    per_user_limit = table.Column<int>(type: "integer", nullable: false),
                    total_seats = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shows", x => x.id);
                    table.CheckConstraint("ck_shows_per_user_limit", "per_user_limit > 0");
                    table.CheckConstraint("ck_shows_price_paise", "price_paise >= 0");
                    table.CheckConstraint("ck_shows_total_seats", "total_seats > 0");
                });

            migrationBuilder.CreateTable(
                name: "reservations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    show_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    amount_paise = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservations", x => x.id);
                    table.CheckConstraint("ck_reservations_amount_paise", "amount_paise >= 0");
                    table.CheckConstraint("ck_reservations_status", "status IN ('confirmed', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_reservations_shows_show_id",
                        column: x => x.show_id,
                        principalTable: "shows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "show_user_limits",
                columns: table => new
                {
                    show_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    reserved_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_show_user_limits", x => new { x.show_id, x.user_id });
                    table.CheckConstraint("ck_show_user_limits_reserved_count", "reserved_count >= 0");
                    table.ForeignKey(
                        name: "fk_show_user_limits_shows_show_id",
                        column: x => x.show_id,
                        principalTable: "shows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    show_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_idempotency_keys_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_idempotency_keys_shows_show_id",
                        column: x => x.show_id,
                        principalTable: "shows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "seats",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    show_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, collation: "C"),
                    position = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seats", x => x.id);
                    table.CheckConstraint("ck_seats_owner_matches_status", "(status = 'available') = (reservation_id IS NULL)");
                    table.CheckConstraint("ck_seats_status", "status IN ('available', 'held', 'confirmed')");
                    table.ForeignKey(
                        name: "fk_seats_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_seats_shows_show_id",
                        column: x => x.show_id,
                        principalTable: "shows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reservation_seats",
                columns: table => new
                {
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservation_seats", x => new { x.reservation_id, x.seat_id });
                    table.ForeignKey(
                        name: "fk_reservation_seats_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reservation_seats_seats_seat_id",
                        column: x => x.seat_id,
                        principalTable: "seats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_reservation_id",
                table: "idempotency_keys",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_show_id_user_id_idempotency_key",
                table: "idempotency_keys",
                columns: new[] { "show_id", "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reservation_seats_seat_id",
                table: "reservation_seats",
                column: "seat_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservations_show_id_user_id",
                table: "reservations",
                columns: new[] { "show_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_seats_reservation_id",
                table: "seats",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "ix_seats_show_id_seat_number",
                table: "seats",
                columns: new[] { "show_id", "seat_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shows_created_at",
                table: "shows",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "reservation_seats");

            migrationBuilder.DropTable(
                name: "show_user_limits");

            migrationBuilder.DropTable(
                name: "seats");

            migrationBuilder.DropTable(
                name: "reservations");

            migrationBuilder.DropTable(
                name: "shows");
        }
    }
}
