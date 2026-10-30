using LiteDB;
using System;
using System.Linq;

namespace litedbcli
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: litedbcli <connectionArgs> [<command>...]");
                Console.WriteLine();
                Console.WriteLine("  <connectionArgs>  Database file path, or a full connection string");
                Console.WriteLine("                    (e.g. \"Filename=test.db;Password=secret;Connection=shared\")");
                Console.WriteLine("  <command>         Optional command to run once and exit.");
                Console.WriteLine("                    Omit to enter interactive mode.");
                Console.WriteLine();
                Console.WriteLine("Examples:");
                Console.WriteLine("  litedbcli test.db                      Interactive mode");
                Console.WriteLine("  litedbcli test.db \"show tables\"");
                Console.WriteLine("  litedbcli test.db \"select * from Users limit 10\"");
                return 2;
            }

            var connstr = BuildConnectionString(args[0]);
            var command = args.Length > 1 ? string.Join(" ", args.Skip(1)) : null;

            try
            {
                using (LiteDatabase db = new LiteDatabase(connstr))
                {
                    if (command == null)
                        return Interactive(db);
                    Perform(db, command);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        static string BuildConnectionString(string arg)
        {
            if (arg.IndexOf('=') >= 0)
                return arg;
            return $"Filename=\"{arg}\"; Connection=shared";
        }

        static int Interactive(LiteDatabase db)
        {
            while (true)
            {
                Console.Write("> ");
                var cmd = Console.ReadLine()?.Trim();
                if (cmd == "exit" || cmd == "quit") break;
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                try
                {
                    Perform(db, cmd);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error running command: {ex.Message}");
                }
            }
            return 0;
        }

        static void Perform(LiteDatabase db, string cmd)
        {
            if (cmd.Length == 0) return;

            if (cmd.Equals("SHOW TABLES", StringComparison.OrdinalIgnoreCase) ||
                cmd.Equals("LIST COLLECTIONS", StringComparison.OrdinalIgnoreCase))
            {
                var collections = db.GetCollectionNames();
                foreach (var col in collections)
                {
                    Console.WriteLine(col);
                }
                Console.WriteLine($"Total collections: {collections.Count()}");
                return;
            }

            var reader = db.Execute(cmd);
            int affectedRows = 0;
            bool headerPrinted = false;
            while (reader.Read())
            {
                if (cmd.StartsWith("select", StringComparison.OrdinalIgnoreCase))
                {
                    var document = reader.Current.AsDocument;
                    if (!headerPrinted)
                    {
                        Console.WriteLine(string.Join("\t", document.Keys));
                        Console.WriteLine(new string('-', 50));
                        headerPrinted = true;
                    }
                    Console.WriteLine(string.Join("\t", document.Values));
                    Console.WriteLine("-----------");
                }
                affectedRows++;
            }
            Console.WriteLine("Total records affected: " + affectedRows);
        }
    }
}
