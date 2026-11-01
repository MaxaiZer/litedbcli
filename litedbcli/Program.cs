using LiteDB;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace litedbcli
{
    class Program
    {
        const int DefaultLimit = 1000;

        static int Main(string[] args)
        {
            var opts = new Options();

            if (!opts.Parse(args))
            {
                if (opts.Help)
                {
                    PrintUsage(Console.Out);
                    return 0;
                }
                Console.Error.WriteLine("Error: " + opts.Error);
                PrintUsage(Console.Error);
                return 2;
            }

            if (opts.Serve && opts.Command != null)
            {
                Console.Error.WriteLine("Error: --serve does not accept a command");
                return 2;
            }

            if (opts.Serve && opts.Json == false)
                opts.Json = true;

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            if (Console.IsOutputRedirected)
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            if (Console.IsInputRedirected)
                Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8));

            try
            {
                using (var db = new LiteDatabase(opts.Connection))
                {
                    if (opts.Serve)
                        return Serve(db, opts);

                    if (opts.Command == null)
                        return Interactive(db, opts);

                    EnsureWritable(opts.Command, opts.Connection.ReadOnly);

                    return opts.Json ? RunJson(db, opts) : RunText(db, opts);
                }
            }
            catch (Exception ex)
            {
                if (opts.Json)
                    WriteError(ex, null);
                else
                    Console.Error.WriteLine("Error: " + ex.Message);
                return 1;
            }
        }

        static int RunJson(LiteDatabase db, Options opts)
        {
            using (var buffer = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    WriteResult(writer, db, opts.Command, opts.Limit, null);
                }
                Console.Out.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
                Console.Out.Flush();
            }
            return 0;
        }

        static int RunText(LiteDatabase db, Options opts)
        {
            using (var reader = db.Execute(opts.Command))
            {
                var isQuery = IsQuery(opts.Command);
                var count = 0;

                while (reader.Read() && count < opts.Limit)
                {
                    var value = reader.Current;

                    if (isQuery)
                    {
                        var doc = value.IsDocument ? value.AsDocument : new BsonDocument { ["value"] = value };

                        if (count == 0)
                        {
                            Console.WriteLine(string.Join("\t", doc.Keys));
                            Console.WriteLine(new string('-', 50));
                        }
                        Console.WriteLine(string.Join("\t", doc.Values));
                        Console.WriteLine("-----------");
                    }
                    else
                    {
                        Console.WriteLine(ToJson(value));
                    }
                    count++;
                }
            }
            return 0;
        }

        static int Interactive(LiteDatabase db, Options opts)
        {
            while (true)
            {
                Console.Write("> ");
                var cmd = Console.ReadLine()?.Trim();
                if (cmd == "exit" || cmd == "quit") break;
                if (string.IsNullOrWhiteSpace(cmd)) continue;

                try
                {
                    EnsureWritable(cmd, opts.Connection.ReadOnly);

                    if (opts.Json)
                    {
                        using (var buffer = new MemoryStream())
                        {
                            using (var writer = new Utf8JsonWriter(buffer))
                            {
                                WriteResult(writer, db, cmd, opts.Limit, null);
                            }
                            Console.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
                        }
                    }
                    else
                    {
                        RunText(db, new Options
                        {
                            Command = cmd,
                            Limit = opts.Limit
                        });
                    }
                }
                catch (Exception ex)
                {
                    if (opts.Json)
                        WriteError(ex, null);
                    else
                        Console.WriteLine($"Error running command: {ex.Message}");
                }
            }
            return 0;
        }

        static int Serve(LiteDatabase db, Options opts)
        {
            string line;
            while ((line = Console.In.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var shouldClose = false;
                JsonElement id = default;
                string response;

                try
                {
                    using (var buffer = new MemoryStream())
                    {
                        using (var writer = new Utf8JsonWriter(buffer))
                        using (var request = ParseRequest(line, out id))
                        {
                        if (request == null)
                        {
                            WriteEnvelopeStart(writer, id, false);
                            writer.WriteString("error", "invalid JSON request");
                            writer.WriteString("type", "ParseError");
                            writer.WriteEndObject();
                        }
                        else
                        {
                            var op = GetString(request.RootElement, "op") ?? "exec";

                            if (op == "ping")
                            {
                                WriteEnvelopeStart(writer, id, true);
                                writer.WriteString("type", "pong");
                                writer.WriteString("version", typeof(Program).Assembly.GetName().Version?.ToString());
                                writer.WriteEndObject();
                            }
                            else if (op == "collections")
                            {
                                WriteCollections(writer, db, id);
                            }
                            else if (op == "exec" || op == "query")
                            {
                                var sql = GetString(request.RootElement, "sql") ?? GetString(request.RootElement, "command");
                                var limit = GetInt(request.RootElement, "limit") ?? opts.Limit;
                                var sort = GetString(request.RootElement, "sort");
                                var dir = GetString(request.RootElement, "dir");

                                if (string.IsNullOrWhiteSpace(sql))
                                {
                                    WriteEnvelopeStart(writer, id, false);
                                    writer.WriteString("error", "missing 'sql'");
                                    writer.WriteString("type", "RequestError");
                                    writer.WriteEndObject();
                                }
                                else
                                {
                                    EnsureWritable(sql, opts.Connection.ReadOnly);
                                    WriteResult(writer, db, sql, limit, id, sort, dir);
                                }
                            }
                            else if (op == "save")
                            {
                                var collection = GetString(request.RootElement, "collection");
                                var hasDoc = request.RootElement.TryGetProperty("doc", out var docEl)
                                    && docEl.ValueKind == JsonValueKind.Object;

                                if (string.IsNullOrWhiteSpace(collection) || !hasDoc)
                                {
                                    WriteEnvelopeStart(writer, id, false);
                                    writer.WriteString("error", "save requires 'collection' and 'doc' object");
                                    writer.WriteString("type", "RequestError");
                                    writer.WriteEndObject();
                                }
                                else
                                {
                                    SaveDoc(writer, db, opts, id, collection, docEl);
                                }
                            }
                            else if (op == "close")
                            {
                                WriteEnvelopeStart(writer, id, true);
                                writer.WriteString("type", "bye");
                                writer.WriteEndObject();
                                shouldClose = true;
                            }
                            else
                            {
                                WriteEnvelopeStart(writer, id, false);
                                writer.WriteString("error", "unknown op: " + op);
                                writer.WriteString("type", "RequestError");
                                writer.WriteEndObject();
                            }
                        }
                        }

                        response = Encoding.UTF8.GetString(buffer.ToArray());
                    }
                }
                catch (Exception ex)
                {
                    response = ErrorJson(ex, id);
                }

                Console.Out.WriteLine(response);
                Console.Out.Flush();

                if (shouldClose) return 0;
            }
            return 0;
        }

        static JsonDocument ParseRequest(string line, out JsonElement id)
        {
            id = default;
            try
            {
                var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    doc.Dispose();
                    return null;
                }
                if (doc.RootElement.TryGetProperty("id", out var value))
                    id = value.Clone();
                return doc;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        static void WriteResult(Utf8JsonWriter writer, LiteDatabase db, string command, int limit, JsonElement? id, string sort = null, string dir = null)
        {
            var sw = Stopwatch.StartNew();
            var cmd = command.Trim().TrimEnd(';').Trim();
            var sortField = IsQuery(cmd) ? sort : null;
            var execSql = cmd;
            if (!string.IsNullOrWhiteSpace(sortField))
                execSql = StripLimitOffset(cmd);

            if (IsShowTables(cmd))
            {
                WriteCollections(writer, db, id);
                return;
            }

            using (var reader = db.Execute(execSql))
            {
                var isQuery = IsQuery(cmd);

                if (isQuery)
                {
                    var docs = new List<BsonDocument>();
                    var truncated = false;

                    while (reader.Read())
                    {
                        if (sortField == null && docs.Count >= limit)
                        {
                            truncated = true;
                            break;
                        }

                        var value = reader.Current;
                        docs.Add(value.IsDocument ? value.AsDocument : new BsonDocument { ["value"] = value });
                    }

                    if (sortField != null)
                    {
                        var descending = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);
                        docs.Sort((a, b) =>
                        {
                            BsonValue va = a.TryGetValue(sortField, out var av) ? av : BsonValue.Null;
                            BsonValue vb = b.TryGetValue(sortField, out var bv) ? bv : BsonValue.Null;
                            var comparison = va.CompareTo(vb);
                            return descending ? -comparison : comparison;
                        });
                        truncated = docs.Count > limit;
                        if (truncated) docs.RemoveRange(limit, docs.Count - limit);
                    }

                    var columns = new List<string>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var doc in docs)
                    {
                        foreach (var key in doc.Keys)
                        {
                            if (seen.Add(key)) columns.Add(key);
                        }
                    }

                    WriteEnvelopeStart(writer, id, true);
                    writer.WriteString("type", "query");
                    if (reader.Collection != null)
                        writer.WriteString("collection", reader.Collection);
                    WriteStringArray(writer, "columns", columns);
                    writer.WriteNumber("count", docs.Count);
                    writer.WriteBoolean("truncated", truncated);

                    writer.WritePropertyName("rows");
                    writer.WriteStartArray();
                    foreach (var doc in docs)
                        writer.WriteRawValue(ToJson(doc), true);
                    writer.WriteEndArray();
                }
                else
                {
                    BsonValue value = BsonValue.Null;
                    var hasValue = reader.Read();
                    if (hasValue) value = reader.Current;

                    WriteEnvelopeStart(writer, id, true);
                    writer.WriteString("type", "result");
                    writer.WritePropertyName("value");
                    writer.WriteRawValue(ToJson(value), true);
                }
            }

            sw.Stop();
            writer.WriteNumber("elapsed_ms", sw.Elapsed.TotalMilliseconds);
            writer.WriteEndObject();
        }

        static void WriteCollections(Utf8JsonWriter writer, LiteDatabase db, JsonElement? id)
        {
            var names = db.GetCollectionNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

            WriteEnvelopeStart(writer, id, true);
            writer.WriteString("type", "collections");
            WriteStringArray(writer, "collections", names);
            writer.WriteNumber("count", names.Length);
            writer.WriteEndObject();
        }

        static void WriteEnvelopeStart(Utf8JsonWriter writer, JsonElement? id, bool ok)
        {
            writer.WriteStartObject();
            if (id.HasValue && id.Value.ValueKind != JsonValueKind.Undefined)
            {
                writer.WritePropertyName("id");
                writer.WriteRawValue(id.Value.GetRawText(), true);
            }
            writer.WriteBoolean("ok", ok);
        }

        static string ErrorJson(Exception ex, JsonElement id)
        {
            using (var buffer = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    WriteEnvelopeStart(writer, id.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : id, false);
                    writer.WriteString("error", ex.Message);
                    writer.WriteString("type", ex.GetType().Name);
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        static void WriteError(Exception ex, JsonElement? id)
        {
            Console.Out.WriteLine(ErrorJson(ex, id ?? default));
            Console.Out.Flush();
        }

        static void WriteStringArray(Utf8JsonWriter writer, string name, IEnumerable<string> values)
        {
            writer.WritePropertyName(name);
            writer.WriteStartArray();
            foreach (var value in values)
                writer.WriteStringValue(value);
            writer.WriteEndArray();
        }

        static string ToJson(BsonValue value)
        {
            return LiteDB.JsonSerializer.Serialize(value);
        }

        static void SaveDoc(Utf8JsonWriter writer, LiteDatabase db, Options opts, JsonElement? id, string collection, JsonElement doc)
        {
            var sw = Stopwatch.StartNew();

            if (opts.Connection.ReadOnly)
                throw new InvalidOperationException("database is opened read-only");

            var bson = SaveOps.FromJson(doc.GetRawText());
            var updated = SaveOps.Save(db, collection, bson);

            WriteEnvelopeStart(writer, id, true);
            writer.WriteString("type", "result");
            writer.WriteBoolean("updated", updated);
            sw.Stop();
            writer.WriteNumber("elapsed_ms", sw.Elapsed.TotalMilliseconds);
            writer.WriteEndObject();
        }

        static string StripLimitOffset(string sql)
        {
            var pattern = new Regex(@"\s+(?:limit|offset)\s+\d+\s*$", RegexOptions.IgnoreCase);
            var result = sql;
            while (pattern.IsMatch(result))
                result = pattern.Replace(result, "");
            return result;
        }

        static bool IsQuery(string cmd)
        {
            var trimmed = cmd.TrimStart();
            return trimmed.StartsWith("select", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("explain", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsShowTables(string cmd)
        {
            var normalized = cmd.Trim().TrimEnd(';').Trim();
            return normalized.Equals("show tables", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals("show collections", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals("list collections", StringComparison.OrdinalIgnoreCase);
        }

        static string GetString(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        static int? GetInt(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return Math.Clamp(number, 1, 100000);
            return null;
        }

        static void PrintUsage(TextWriter output)
        {
            output.WriteLine("Usage: litedbcli <connection> [command] [options]");
            output.WriteLine();
            output.WriteLine("  <connection>      Database file path or connection string");
            output.WriteLine("                    (e.g. \"Filename=test.db;Password=secret\")");
            output.WriteLine("  [command]         SQL command to run once. Omit for interactive mode.");
            output.WriteLine();
            output.WriteLine("Options:");
            output.WriteLine("  --json            Print one JSON object instead of text");
            output.WriteLine("  --serve           Read NDJSON requests from stdin (implies --json)");
            output.WriteLine("  --limit <n>       Max rows returned (default: " + DefaultLimit + ")");
            output.WriteLine("  --readonly        Open database in read-only mode");
            output.WriteLine("  --shared          Use Connection=Shared (safe for multiple processes)");
            output.WriteLine("  --direct          Use Connection=Direct (default, fastest)");
            output.WriteLine("  --password <pwd>  Database password (or LITEDBCLI_PASSWORD)");
            output.WriteLine("  --help            Show this help");
            output.WriteLine();
            output.WriteLine("Examples:");
            output.WriteLine("  litedbcli test.db \"show tables\"");
            output.WriteLine("  litedbcli test.db --json \"select $ from Users limit 10\"");
            output.WriteLine("  litedbcli test.db --serve");
        }

        sealed class Options
        {
            public bool Json { get; set; }
            public bool Serve { get; set; }
            public bool Help { get; set; }
            public string Error { get; set; }
            public int Limit { get; set; } = DefaultLimit;
            public string Command { get; set; }
            public ConnectionString Connection { get; set; }

            public bool Parse(string[] args)
            {
                if (args.Length == 0)
                {
                    Help = true;
                    return false;
                }

                string connectionArg = null;
                var commandParts = new List<string>();
                string password = null;
                ConnectionType? mode = null;
                bool? readOnly = null;

                for (var i = 0; i < args.Length; i++)
                {
                    var arg = args[i];

                    switch (arg)
                    {
                        case "--json":
                            Json = true;
                            continue;
                        case "--serve":
                            Serve = true;
                            continue;
                        case "--readonly":
                            readOnly = true;
                            continue;
                        case "--shared":
                            mode = ConnectionType.Shared;
                            continue;
                        case "--direct":
                            mode = ConnectionType.Direct;
                            continue;
                        case "--help":
                        case "-h":
                            Help = true;
                            return false;
                    }

                    if (arg == "--password" || arg.StartsWith("--password=", StringComparison.Ordinal))
                    {
                        if (arg.Contains('='))
                        {
                            password = arg.Substring(arg.IndexOf('=') + 1);
                        }
                        else
                        {
                            password = Next(args, ref i, "--password", out var passwordError);
                            if (passwordError != null)
                            {
                                Error = passwordError;
                                return false;
                            }
                        }
                        continue;
                    }

                    if (arg == "--limit" || arg.StartsWith("--limit=", StringComparison.Ordinal))
                    {
                        string value;
                        if (arg.Contains('='))
                        {
                            value = arg.Substring(arg.IndexOf('=') + 1);
                        }
                        else
                        {
                            value = Next(args, ref i, "--limit", out var limitError);
                            if (limitError != null)
                            {
                                Error = limitError;
                                return false;
                            }
                        }
                        if (!int.TryParse(value, out var limit) || limit <= 0)
                        {
                            Error = "--limit must be a positive integer";
                            return false;
                        }
                        Limit = Math.Min(limit, 100000);
                        continue;
                    }

                    if (arg.StartsWith("-", StringComparison.Ordinal) && arg != "-")
                    {
                        Error = "unknown option: " + arg;
                        return false;
                    }

                    if (connectionArg == null)
                        connectionArg = arg;
                    else
                        commandParts.Add(arg);
                }

                if (connectionArg == null)
                {
                    Error = "missing database file or connection string";
                    return false;
                }

                Connection = BuildConnectionString(connectionArg);

                if (password == null && string.IsNullOrEmpty(Connection.Password))
                    password = Environment.GetEnvironmentVariable("LITEDBCLI_PASSWORD");

                if (password != null)
                    Connection.Password = password;
                if (readOnly.HasValue)
                    Connection.ReadOnly = readOnly.Value;
                if (mode.HasValue)
                    Connection.Connection = mode.Value;

                Command = commandParts.Count > 0 ? string.Join(" ", commandParts) : null;

                return true;
            }

            static string Next(string[] args, ref int i, string name, out string error)
            {
                error = null;
                if (i + 1 >= args.Length)
                {
                    error = name + " requires a value";
                    return null;
                }
                return args[++i];
            }
        }

        static ConnectionString BuildConnectionString(string arg)
        {
            var first = arg.Split(';')[0];
            var eq = first.IndexOf('=');
            var key = eq > 0 ? first.Substring(0, eq).Trim() : null;

            var looksLikeConnectionString = arg.Contains(';') || IsConnectionKey(key);

            return looksLikeConnectionString
                ? new ConnectionString(arg)
                : new ConnectionString { Filename = arg };
        }

        static bool IsConnectionKey(string key)
        {
            if (key == null) return false;

            switch (key.ToLowerInvariant())
            {
                case "filename":
                case "password":
                case "connection":
                case "readonly":
                case "upgrade":
                case "initial size":
                case "collation":
                case "auto-rebuild":
                    return true;
                default:
                    return false;
            }
        }

        static void EnsureWritable(string command, bool readOnly)
        {
            if (!readOnly) return;

            var cmd = command.TrimStart();
            if (cmd.StartsWith("select", StringComparison.OrdinalIgnoreCase) ||
                cmd.StartsWith("explain", StringComparison.OrdinalIgnoreCase) ||
                cmd.StartsWith("show ", StringComparison.OrdinalIgnoreCase) ||
                cmd.StartsWith("list ", StringComparison.OrdinalIgnoreCase) ||
                cmd.StartsWith("pragma", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new InvalidOperationException("database is opened read-only");
        }
    }
}
