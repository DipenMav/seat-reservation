using Microsoft.EntityFrameworkCore;

namespace SeatReservation.Api.Infrastructure.Persistence;

/// <summary>
/// EF Core owns the schema (migrations) and the simple reads. The concurrency-critical writes
/// (reserve / cancel) are explicit SQL in the feature handlers so the atomic guard is visible.
/// Every invariant that can be expressed as a constraint is declared here as a backstop.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Show> Shows => Set<Show>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<ReservationSeat> ReservationSeats => Set<ReservationSeat>();
    public DbSet<ShowUserLimit> ShowUserLimits => Set<ShowUserLimit>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Show>(e =>
        {
            e.ToTable("shows", t =>
            {
                t.HasCheckConstraint("ck_shows_price_paise", "price_paise >= 0");
                t.HasCheckConstraint("ck_shows_per_user_limit", "per_user_limit > 0");
                t.HasCheckConstraint("ck_shows_total_seats", "total_seats > 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<Seat>(e =>
        {
            e.ToTable("seats", t =>
            {
                t.HasCheckConstraint("ck_seats_status", "status IN ('available', 'held', 'confirmed')");
                // Owned <=> not available. A confirmed seat without an owner, or an available
                // seat that still points at a reservation, cannot be committed.
                t.HasCheckConstraint("ck_seats_owner_matches_status",
                    "(status = 'available') = (reservation_id IS NULL)");
            });
            e.HasKey(x => x.Id);
            // "C" collation: ORDER BY seat_number in SQL matches ordinal ordering in C#.
            e.Property(x => x.SeatNumber).HasMaxLength(50).IsRequired().UseCollation("C");
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.ShowId, x.SeatNumber }).IsUnique();
            e.HasIndex(x => x.ReservationId);
            e.HasOne<Show>().WithMany().HasForeignKey(x => x.ShowId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Reservation>().WithMany().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Reservation>(e =>
        {
            e.ToTable("reservations", t =>
            {
                t.HasCheckConstraint("ck_reservations_amount_paise", "amount_paise >= 0");
                t.HasCheckConstraint("ck_reservations_status", "status IN ('confirmed', 'cancelled')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.HasIndex(x => new { x.ShowId, x.UserId });
            e.HasOne<Show>().WithMany().HasForeignKey(x => x.ShowId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ReservationSeat>(e =>
        {
            e.ToTable("reservation_seats");
            e.HasKey(x => new { x.ReservationId, x.SeatId });
            e.HasIndex(x => x.SeatId);
            e.HasOne<Reservation>().WithMany().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Seat>().WithMany().HasForeignKey(x => x.SeatId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ShowUserLimit>(e =>
        {
            e.ToTable("show_user_limits", t =>
                t.HasCheckConstraint("ck_show_user_limits_reserved_count", "reserved_count >= 0"));
            e.HasKey(x => new { x.ShowId, x.UserId });
            e.Property(x => x.UserId).HasMaxLength(100);
            e.HasOne<Show>().WithMany().HasForeignKey(x => x.ShowId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IdempotencyKey>(e =>
        {
            e.ToTable("idempotency_keys");
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasColumnName("idempotency_key").HasMaxLength(200).IsRequired();
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.ShowId, x.UserId, x.Key }).IsUnique();
            e.HasOne<Show>().WithMany().HasForeignKey(x => x.ShowId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Reservation>().WithMany().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
