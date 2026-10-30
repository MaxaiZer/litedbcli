using LiteDB;
using System;
using System.Text;
using System.Text.Json;

namespace litedbcli
{
    public static class SaveOps
    {
        public static BsonValue FromJson(string extendedJson)
        {
            // LiteDB 5.0.21 JsonReader only recognizes a "$"-wrapper when it is the
            // FIRST key of an object; with more keys after it the parse breaks
            // ("Unexpected token ,"). Reorder objects so "$"-keys come last.
            var normalized = ReorderDollarKeysLast(extendedJson);
            return LiteDB.JsonSerializer.Deserialize(normalized);
        }

        public static string ToJson(BsonValue value)
        {
            return LiteDB.JsonSerializer.Serialize(value);
        }

        public static bool Save(LiteDatabase db, string collection, BsonValue doc)
        {
            if (doc == null || !doc.IsDocument)
                throw new LiteException(LiteException.INVALID_DATA_TYPE, "doc must be an object");

            var idValue = doc.AsDocument["_id"];
            if (idValue == null || idValue.IsNull)
                throw new LiteException(LiteException.INVALID_DATA_TYPE, "doc must contain '_id'");

            var col = db.GetCollection<BsonDocument>(collection);
            return col.Update(idValue, doc.AsDocument);
        }

        // Walk the JSON and, in every object, move keys starting with '$' to the end.
        // Values keep their original text (no reserialization, no precision loss).
        public static string ReorderDollarKeysLast(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            Write(doc.RootElement, sb);
            return sb.ToString();
        }

        private static void Write(JsonElement el, StringBuilder sb)
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                sb.Append(el.GetRawText());
                return;
            }

            var dollar = new StringBuilder();
            sb.Append('{');
            var first = true;
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Name.Length > 0 && prop.Name[0] == '$')
                {
                    AppendKey(dollar, prop.Name);
                    dollar.Append(':');
                    Write(prop.Value, dollar);
                    dollar.Append(',');
                    continue;
                }
                if (!first) sb.Append(',');
                first = false;
                AppendKey(sb, prop.Name);
                sb.Append(':');
                Write(prop.Value, sb);
            }
            if (dollar.Length > 0)
            {
                if (!first) sb.Append(',');
                sb.Append(dollar.ToString(0, dollar.Length - 1)); // drop trailing ','
            }
            sb.Append('}');
        }
        private static void AppendKey(StringBuilder sb, string key)
        {
            sb.Append('"');
            foreach (var c in key)
            {
                if (c == '"' || c == '\u005C')
                    sb.Append('\u005C');
                sb.Append(c);
            }
            sb.Append('"');
        }
    }
}