using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QuizRoom.Web.Data;

/// <summary>Контекст базы данных: связывает классы C# с таблицами MySQL.</summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Host> Hosts => Set<Host>();
    public DbSet<Quiz> Quizzes => Set<Quiz>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<AnswerOption> AnswerOptions => Set<AnswerOption>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Кодировка utf8mb4 (русский язык и эмодзи)
        modelBuilder.UseCollation("utf8mb4_unicode_ci");

        // Таблица hosts: логин уникальный
        modelBuilder.Entity<Host>(e =>
        {
            e.ToTable("hosts");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Login).HasColumnName("login").HasMaxLength(50).IsRequired();
            e.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(255).IsRequired();
            e.HasIndex(x => x.Login).IsUnique();
        });

        // Таблица quizzes: при удалении ведущего удаляются его викторины
        modelBuilder.Entity<Quiz>(e =>
        {
            e.ToTable("quizzes");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.HostId).HasColumnName("host_id");
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(100).IsRequired();
            e.HasOne(x => x.Host).WithMany(h => h.Quizzes)
                .HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.HostId).HasDatabaseName("idx_quizzes_host");
        });

        // Таблица questions: уникальная пара (викторина, порядковый номер), время 5-120 с
        modelBuilder.Entity<Question>(e =>
        {
            e.ToTable("questions", t =>
                t.HasCheckConstraint("CK_questions_time_limit", "time_limit_sec BETWEEN 5 AND 120"));
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.QuizId).HasColumnName("quiz_id");
            e.Property(x => x.OrderNum).HasColumnName("order_num");
            e.Property(x => x.Text).HasColumnName("text").HasMaxLength(500).IsRequired();
            e.Property(x => x.TimeLimitSec).HasColumnName("time_limit_sec");
            e.HasOne(x => x.Quiz).WithMany(q => q.Questions)
                .HasForeignKey(x => x.QuizId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.QuizId, x.OrderNum }).IsUnique().HasDatabaseName("uq_question_order");
        });

        // Таблица answer_options
        modelBuilder.Entity<AnswerOption>(e =>
        {
            e.ToTable("answer_options");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.QuestionId).HasColumnName("question_id");
            e.Property(x => x.Text).HasColumnName("text").HasMaxLength(200).IsRequired();
            e.Property(x => x.IsCorrect).HasColumnName("is_correct");
            e.HasOne(x => x.Question).WithMany(q => q.Options)
                .HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.QuestionId).HasDatabaseName("idx_options_question");
        });
    }
}

/// <summary>
/// Нужна только для команд dotnet ef (создание миграций): говорит, как подключиться к локальной базе.
/// В работе приложения не участвует. Пароль только для локальной машины (из docker-compose.yml).
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(
                "Server=localhost;Port=3306;Database=quizroom;User=quizroom;Password=quizroom_dev;",
                new MySqlServerVersion(new Version(8, 0, 36)))
            .Options;
        return new AppDbContext(options);
    }
}