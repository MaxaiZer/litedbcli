// Node test for frontend/lib.js (pure helpers, no DOM).
const assert = require("node:assert");
const gui = require("../frontend/lib.js");

// toModel of a realistic doc
const doc = {
  _id: { $oid: "64f8a1b2c3d4e5f60718293a" },
  name: "привет 日本語 🚀",
  count: 5,
  big: { $numberLong: "9223372036854775807" },
  price: { $numberDecimal: "123.4567890123456" },
  ratio: 3.14,
  created: { $date: 1704110645678 },
  data: { $binary: "AQID+g==" },
  flag: true,
  nothing: null,
  tags: ["a", { $numberLong: "42" }],
  nested: { x: 1, y: { z: true } }
};

const model = gui.toModel(doc);
assert.strictEqual(model.type, "object");
const roundTripped = gui.fromModel(model);
// identical except $date may be normalized from epoch-ms to ISO string (both valid extended JSON)
const norm = (v) => JSON.parse(JSON.stringify(v, (k, val) =>
  val && typeof val === "object" && "$date" in val && typeof val.$date === "number"
    ? { $date: new Date(val.$date).toISOString() } : val));
assert.deepStrictEqual(norm(roundTripped), norm(doc), "round-trip must be identical (dates normalized)");
assert.strictEqual(new Date(roundTripped.created.$date).getTime(), 1704110645678);

// type-specific checks
const fields = Object.fromEntries(model.value.map(f => [f.key, f.model]));
assert.strictEqual(fields._id.type, "oid");
assert.strictEqual(fields.big.type, "long");
assert.strictEqual(fields.price.type, "decimal");
assert.strictEqual(fields.count.type, "int");
assert.strictEqual(fields.ratio.type, "double");
assert.strictEqual(fields.created.type, "date");
assert.strictEqual(fields.data.type, "binary");
assert.strictEqual(fields.nested.type, "object");
assert.strictEqual(fields.tags.type, "array");

// validation errors
assert.throws(() => gui.fromModel({ type: "long", value: "abc" }));
assert.throws(() => gui.fromModel({ type: "int", value: "1.5" }));
assert.throws(() => gui.fromModel({ type: "decimal", value: "1e5" }));
assert.throws(() => gui.fromModel({ type: "oid", value: "xyz" }));
assert.throws(() => gui.fromModel({ type: "binary", value: "abc" }));
assert.throws(() => gui.fromModel({ type: "date", value: "nope" }));

// duplicate field names rejected
assert.throws(() => gui.fromModel({
  type: "object",
  value: [
    { key: "a", model: { type: "int", value: "1" } },
    { key: "a", model: { type: "int", value: "2" } }
  ]
}));

// minValue / maxValue
assert.deepStrictEqual(gui.fromModel({ type: "minValue", value: null }), { $minValue: "1" });
assert.deepStrictEqual(gui.fromModel({ type: "maxValue", value: null }), { $maxValue: "1" });

// date helpers
assert.strictEqual(gui.dateToInput("2024-01-02T03:04:05.678Z").length, 19);
assert.strictEqual(new Date(gui.inputToDate("2024-01-02T03:04:05")).toISOString(), "2024-01-02T03:04:05.000Z");

// null != missing != ""
assert.strictEqual(gui.toModel(null).type, "null");
assert.strictEqual(gui.toModel("").type, "string");
assert.strictEqual(gui.fromModel({ type: "null", value: null }), null);
assert.strictEqual(gui.fromModel({ type: "string", value: "" }), "");

// idempotency over a mixed value tree
const mixed = gui.toModel([1, "two", true, null, { a: { $numberDecimal: "1.234567890123456" } }]);
assert.deepStrictEqual(gui.fromModel(mixed), [1, "two", true, null, { a: { $numberDecimal: "1.234567890123456" } }]);

console.log("lib.js tests: all passed");
