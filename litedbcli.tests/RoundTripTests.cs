using LiteDB;
using System;
using System.Collections.Generic;
using Xunit;

namespace litedbcli.tests
{
    public class RoundTripTests
    {
        public static IEnumerable<object[]> Fixtures()
        {
            yield return new object[] { 42 };
            yield return new object[] { -1 };
            yield return new object[] { int.MaxValue };
            yield return new object[] { 5000000000L };
            yield return new object[] { long.MaxValue };
            yield return new object[] { -9223372036854775807L };
            yield return new object[] { 3.14 };
            yield return new object[] { -0.5 };
            yield return new object[] { 123456789012345m };
            yield return new object[] { -0.01m };
            yield return new object[] { "hello" };
            yield return new object[] { "" };
            yield return new object[] { "привет 日本語 🚀" };
            yield return new object[] { true };
            yield return new object[] { false };
            yield return new object[] { BsonValue.Null };
            yield return new object[] { new DateTime(2024, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc) };
            yield return new object[] { ObjectId.NewObjectId() };
            yield return new object[] { new Guid("01234567-89ab-cdef-0123-456789abcdef") };
            yield return new object[] { new byte[] { 1, 2, 3, 250, 255 } };
            yield return new object[] { new BsonArray { 1, "two", true, new BsonDocument { ["x"] = 10 } } };
            yield return new object[]
            {
                new BsonDocument
                {
                    ["_id"] = 1,
                    ["name"] = "nested",
                    ["doc"] = new BsonDocument { ["a"] = 1.5, ["b"] = new BsonArray { 1L, "two" } },
                    ["missing"] = BsonValue.Null
                }
            };
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Serialize_Deserialize_ShouldBeIdempotent(object value)
        {
            var bson = value is BsonValue bv ? bv :
                value is int i ? new BsonValue(i) :
                value is long l ? new BsonValue(l) :
                value is double d ? new BsonValue(d) :
                value is decimal m ? new BsonValue(m) :
                value is string str ? new BsonValue(str) :
                value is bool b ? new BsonValue(b) :
                value is DateTime dt ? new BsonValue(dt) :
                value is ObjectId oid ? new BsonValue(oid) :
                value is Guid g ? new BsonValue(g) :
                value is byte[] bytes ? new BsonValue(bytes) :
                throw new System.InvalidOperationException("unsupported fixture type");

            var s1 = SaveOps.ToJson(bson);
            var v2 = SaveOps.FromJson(s1);
            var s2 = SaveOps.ToJson(v2);

            Assert.Equal(bson.Type, v2.Type);
            Assert.Equal(s1, s2);
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void RoundTrip_ShouldPreserveValue(object value)
        {
            var bson = value is BsonValue bv ? bv :
                value is int i ? new BsonValue(i) :
                value is long l ? new BsonValue(l) :
                value is double d ? new BsonValue(d) :
                value is decimal m ? new BsonValue(m) :
                value is string str ? new BsonValue(str) :
                value is bool b ? new BsonValue(b) :
                value is DateTime dt ? new BsonValue(dt) :
                value is ObjectId oid ? new BsonValue(oid) :
                value is Guid g ? new BsonValue(g) :
                value is byte[] bytes ? new BsonValue(bytes) :
                throw new System.InvalidOperationException("unsupported fixture type");
            var v2 = SaveOps.FromJson(SaveOps.ToJson(bson));

            if (bson.Type == BsonType.DateTime)
            {
                Assert.Equal(bson.AsDateTime.ToUniversalTime(), v2.AsDateTime.ToUniversalTime());
            }
            else
            {
                Assert.Equal(bson, v2);
            }
        }

        [Fact]
        public void PlainBigInteger_ShouldBecomeInt64()
        {
            var v = SaveOps.FromJson("3000000000");
            Assert.Equal(BsonType.Int64, v.Type);
            Assert.Equal(3000000000L, v.AsInt64);
        }

        [Fact]
        public void PlainInteger_ShouldStayInt32()
        {
            var v = SaveOps.FromJson("42");
            Assert.Equal(BsonType.Int32, v.Type);
        }

        [Fact]
        public void NumberLong_ShouldKeepPrecision()
        {
            var v = SaveOps.FromJson("{\"$numberLong\":\"9223372036854775807\"}");
            Assert.Equal(BsonType.Int64, v.Type);
            Assert.Equal(long.MaxValue, v.AsInt64);
        }

        [Fact]
        public void NumberDecimal_ShouldKeepPrecision()
        {
            var v = SaveOps.FromJson("{\"$numberDecimal\":\"123456789012345\"}");
            Assert.Equal(BsonType.Decimal, v.Type);
            Assert.Equal(123456789012345m, v.AsDecimal);
        }

        [Fact]
        public void Oid_ShouldRoundTrip()
        {
            var v = SaveOps.FromJson("{\"$oid\":\"64f8a1b2c3d4e5f60718293a\"}");
            Assert.Equal(BsonType.ObjectId, v.Type);
            Assert.Equal("64f8a1b2c3d4e5f60718293a", v.AsObjectId.ToString());
        }

        [Fact]
        public void Binary_ShouldRoundTrip()
        {
            var v = SaveOps.FromJson("{\"$binary\":\"AQID+g==\"}");
            Assert.Equal(BsonType.Binary, v.Type);
            Assert.Equal(new byte[] { 1, 2, 3, 250 }, v.AsBinary);
        }

        [Fact]
        public void Date_ShouldRoundTripAsUtc()
        {
            var v = SaveOps.FromJson("{\"$date\":\"2024-01-02T03:04:05.678Z\"}");
            Assert.Equal(BsonType.DateTime, v.Type);
            Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc), v.AsDateTime.ToUniversalTime());
        }

        [Fact]
        public void NullAndEmptyString_AreDistinct()
        {
            Assert.True(SaveOps.FromJson("null").IsNull);
            Assert.Equal(BsonType.String, SaveOps.FromJson("\"\"").Type);
        }

        [Fact]
        public void DoubleWithNoFraction_ShouldStayDouble()
        {
            var v = SaveOps.FromJson(SaveOps.ToJson(new BsonValue(5.0)));
            Assert.Equal(BsonType.Double, v.Type);
            Assert.Equal(5.0, v.AsDouble);
        }

        // LiteDB 5.0.21 JsonReader only supports a $-wrapper as the sole/first key of
        // an object. SaveOps.FromJson must normalize key order so wrappers in any
        // position parse.
        [Fact]
        public void WrapperFirstWithMoreKeys_ShouldParse()
        {
            var v = SaveOps.FromJson(@"{""_a"":""x"",""b"":{""$numberLong"":""7"",""a"":""c""}}");
            Assert.Equal(7L, v.AsDocument["b"]["$numberLong"].AsInt64);
            Assert.Equal("c", v.AsDocument["b"]["a"].AsString);
        }

        [Fact]
        public void WrapperNotFirst_ShouldParse()
        {
            // already worked in LiteDB, must keep working
            var v = SaveOps.FromJson(@"{""_a"":""x"",""b"":{""a"":""c"",""$numberLong"":""7""}}");
            Assert.Equal(7L, v.AsDocument["b"]["$numberLong"].AsInt64);
        }

        [Fact]
        public void ReorderDollarKeysLast_ShouldKeepValuesAndMoveDollarKeys()
        {
            Assert.Equal(
                @"{""_a"":1,""$b"":2}",
                SaveOps.ReorderDollarKeysLast(@"{""$b"":2,""_a"":1}"));
            Assert.Equal(
                @"{""_a"":{""_c"":""z"",""$d"":3}}",
                SaveOps.ReorderDollarKeysLast(@"{""_a"":{""$d"":3,""_c"":""z""}}"));
            // dollar keys only - relative order preserved
            Assert.Equal(
                @"{""$a"":1,""$b"":2}",
                SaveOps.ReorderDollarKeysLast(@"{""$a"":1,""$b"":2}"));
        }
    }
}
