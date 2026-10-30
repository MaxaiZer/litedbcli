using LiteDB;
using System;
using System.IO;
using Xunit;

namespace litedbcli.tests
{
    public class SaveOpsTests : IDisposable
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");

        public SaveOpsTests()
        {
            using (var db = new LiteDatabase($"Filename={_dbPath}"))
            {
                db.GetCollection<BsonDocument>("docs").Insert(new BsonDocument
                {
                    ["_id"] = 1,
                    ["name"] = "original",
                    ["age"] = 5
                });
            }
        }

        public void Dispose()
        {
            try { File.Delete(_dbPath); } catch { }
        }

        [Fact]
        public void Save_ShouldUpdateDocument()
        {
            const string json = @"{
                ""_id"": 1,
                ""name"": ""edited"",
                ""age"": 30,
                ""big"": 9000000000,
                ""pi"": 3.14,
                ""dec"": { ""$numberDecimal"": ""123456789012345"" },
                ""when"": { ""$date"": ""2024-01-02T03:04:05Z"" },
                ""oid"": { ""$oid"": ""64f8a1b2c3d4e5f60718293a"" },
                ""bin"": { ""$binary"": ""d29ybGQ="" },
                ""flag"": true,
                ""nothing"": null,
                ""tags"": [1, ""two"", { ""$numberLong"": ""7"" }]
            }";

            using (var db = new LiteDatabase($"Filename={_dbPath}"))
            {
                var updated = SaveOps.Save(db, "docs", SaveOps.FromJson(json));
                Assert.True(updated);

                var doc = db.GetCollection<BsonDocument>("docs").FindById(1);
                Assert.Equal("edited", doc["name"].AsString);
                Assert.Equal(30, doc["age"].AsInt32);
                Assert.Equal(BsonType.Int64, doc["big"].Type);
                Assert.Equal(9000000000L, doc["big"].AsInt64);
                Assert.Equal(3.14, doc["pi"].AsDouble);
                Assert.Equal(BsonType.Decimal, doc["dec"].Type);
                Assert.Equal(123456789012345m, doc["dec"].AsDecimal);
                Assert.Equal(BsonType.DateTime, doc["when"].Type);
                Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), doc["when"].AsDateTime.ToUniversalTime());
                Assert.Equal(BsonType.ObjectId, doc["oid"].Type);
                Assert.Equal("64f8a1b2c3d4e5f60718293a", doc["oid"].AsObjectId.ToString());
                Assert.Equal(new byte[] { 119, 111, 114, 108, 100 }, doc["bin"].AsBinary);
                Assert.True(doc["flag"].AsBoolean);
                Assert.True(doc["nothing"].IsNull);
                var tags = doc["tags"].AsArray;
                Assert.Equal(BsonType.Int32, tags[0].Type);
                Assert.Equal("two", tags[1].AsString);
                Assert.Equal(BsonType.Int64, tags[2].Type);
                Assert.Equal(7L, tags[2].AsInt64);
            }
        }

        [Fact]
        public void Save_ShouldPreserveFieldOrder()
        {
            const string json = @"{ ""_id"": 1, ""z"": 1, ""a"": 2 }";

            using (var db = new LiteDatabase($"Filename={_dbPath}"))
            {
                Assert.True(SaveOps.Save(db, "docs", SaveOps.FromJson(json)));
                var doc = db.GetCollection<BsonDocument>("docs").FindById(1);
                var keys = new System.Collections.Generic.List<string>();
                foreach (var key in doc.Keys) keys.Add(key);
                Assert.Equal(new[] { "_id", "z", "a" }, keys.ToArray());
            }
        }

        [Fact]
        public void Save_WithMissingId_ShouldThrow()
        {
            using (var db = new LiteDatabase($"Filename={_dbPath}"))
            {
                Assert.Throws<LiteException>(() => SaveOps.Save(db, "docs", SaveOps.FromJson(@"{ ""name"": ""x"" }")));
            }
        }

        [Fact]
        public void Save_WithNonDocument_ShouldThrow()
        {
            using (var db = new LiteDatabase($"Filename={_dbPath}"))
            {
                Assert.Throws<LiteException>(() => SaveOps.Save(db, "docs", SaveOps.FromJson(@"5")));
                Assert.Throws<LiteException>(() => SaveOps.Save(db, "docs", SaveOps.FromJson(@"[1, 2]")));
            }
        }

        [Fact]
        public void Save_UnknownId_ShouldReturnFalse()
        {
            using (var db = new LiteDatabase($"Filename={_dbPath}"))
            {
                Assert.False(SaveOps.Save(db, "docs", SaveOps.FromJson(@"{ ""_id"": 999, ""x"": 1 }")));
            }
        }

        [Fact]
        public void Save_ToMissingCollection_ShouldReturnFalse()
        {
            using (var db = new LiteDatabase($"Filename={_dbPath}"))
            {
                Assert.False(SaveOps.Save(db, "missing", SaveOps.FromJson(@"{ ""_id"": ""abc"", ""v"": 1 }")));
            }
        }
    }
}
