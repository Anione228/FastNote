using Microsoft.EntityFrameworkCore;

public class AppDbContext : DbContext
{
    public DbSet<Note> Notes { get; set; }
    public DbSet<UserSetting> UserSettings { get; set; } 
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Говорим, что наша база данных — это просто файлик с именем "Database.db" в папке проекта
        optionsBuilder.UseSqlite("Data Source=Database.db");
    }
}