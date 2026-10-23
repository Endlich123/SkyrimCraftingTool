using Microsoft.Data.Sqlite;

namespace SkyrimCraftingTool.Model
{
    // Every item.db connection of ItemDBHandler. The database runs in WAL mode, and in WAL
    // synchronous=NORMAL only syncs at checkpoints instead of on every commit - still safe against a
    // crash of the app, only a power loss can cost the last commits. Measured 2026-10-22 on the real
    // 160 MB item.db: 2,000 single-field updates took 1,306 ms with the default (FULL), 39 ms with
    // NORMAL. A preset patch over a whole load order writes ~150,000 of them one by one.
    // The setting is per connection and pooled connections keep it, so it costs one tiny statement
    // per Open.
    public sealed class ItemDbConnection : SqliteConnection
    {
        public ItemDbConnection(string connectionString) : base(connectionString) { }

        public override void Open()
        {
            base.Open();
            using var cmd = CreateCommand();
            cmd.CommandText = "PRAGMA synchronous=NORMAL;";
            cmd.ExecuteNonQuery();
        }
    }
}
