using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services
{
    public class ContainerDefinitionService
    {
        private readonly string _connectionString;

        public ContainerDefinitionService(string connectionString)
        {
            _connectionString = connectionString;
        }

        public List<ContainerRecord> LoadAll()
        {
            var containers = new List<ContainerRecord>();

            using var con = new SqliteConnection(_connectionString);
            con.Open();

            using var cmd = new SqliteCommand("SELECT ContainerKey, Name FROM Container", con);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                containers.Add(new ContainerRecord
                {
                    ContainerKey = reader.GetString(0),
                    Name = reader.GetString(1)
                });
            }

            // LVLI laden
            using var cmdLvli = new SqliteCommand("SELECT ContainerKey, LVLiKey, LVLiName FROM ContainerLVLI", con);
            using var readerLvli = cmdLvli.ExecuteReader();

            while (readerLvli.Read())
            {
                var key = readerLvli.GetString(0);
                var lvli = new ContainerLVLIRecord
                {
                    ContainerKey = key,
                    LVLiKey = readerLvli.GetString(1),
                    LVLiName = readerLvli.GetString(2)
                };

                var container = containers.Find(c => c.ContainerKey == key);
                container?.LVLIEntries.Add(lvli);
            }

            return containers;
        }
    }
}
